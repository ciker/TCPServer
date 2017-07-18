﻿using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TCPServer;
using TCPServer.ServerImplemetation;
using System.Buffers;

namespace TCPServer.Client
{
	public class TCPHttpMessageHandler : HttpMessageHandler
	{
		public TCPHttpMessageHandler() : this(null)
		{

		}
		public TCPHttpMessageHandler(ClientOptions options)
		{
			options = options ?? new ClientOptions();
			Options = options;
		}
		public ClientOptions Options
		{
			get; set;
		}

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			AssertNotDisposed();

			var socket = GetSocket(request.RequestUri);
			try
			{
				return await SendAsyncCore(request, cancellationToken);
			}
			catch(IOException)
			{
				if(!Options.AutoReconnect)
					throw;
			}
			catch(SocketException)
			{
				if(!Options.AutoReconnect)
					throw;
			}
			socket = socket ?? GetSocket(request.RequestUri);
			Disconnect(socket);
			return await SendAsyncCore(request, cancellationToken);
		}

		private async Task<HttpResponseMessage> SendAsyncCore(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var socket = await EnsureConnectedAsync(request.RequestUri, cancellationToken).ConfigureAwait(false);
			var networkStream = new NetworkStream(socket, false);
			using(TCPStream tcpStream = new TCPStream(networkStream)
			{
				MaxArrayLength = Options.MaxArrayLength,
				MaxMessageSize = Options.MaxMessageSize,
				Cancellation = cancellationToken,
				ArrayPool = Options.ArrayPool
			})
			{
				await tcpStream.WriteStringAsync(request.Method.Method).ConfigureAwait(false);

				await tcpStream.WriteStringAsync(request.RequestUri.AbsoluteUri).ConfigureAwait(false);

				if(Options.IncludeHeaders)
				{
					var requestHeaders = request.Headers.ToList();
					var contentHeaders = request.Content?.Headers.ToList() ?? new List<KeyValuePair<string, IEnumerable<string>>>();

					var headers = requestHeaders.Concat(contentHeaders).SelectMany(h => h.Value.Select(v => new
					{
						Key = h.Key,
						Value = v
					})).ToList();

					await tcpStream.WriteVarIntAsync((ulong)headers.Count()).ConfigureAwait(false);

					foreach(var header in headers)
					{
						await tcpStream.WriteStringAsync(header.Key).ConfigureAwait(false);
						await tcpStream.WriteStringAsync(header.Value).ConfigureAwait(false);
					}
				}

				await tcpStream.WriteVarIntAsync(request.Content == null ? 0UL : 1).ConfigureAwait(false);
				if(request.Content != null)
				{
					await tcpStream.WriteVarIntAsync((ulong)request.Content.Headers.ContentLength).ConfigureAwait(false);
					await (await request.Content.ReadAsStreamAsync().ConfigureAwait(false)).CopyToAsync(networkStream, 81920, cancellationToken).ConfigureAwait(false);
				}
				await networkStream.FlushAsync(cancellationToken).ConfigureAwait(false);

				return await GetResponseAsync(tcpStream).ConfigureAwait(false);
			}
		}

		ConcurrentDictionary<IPEndPoint, Socket> _Sockets = new ConcurrentDictionary<IPEndPoint, Socket>();

		private Socket GetSocket(Uri uri)
		{
			IPEndPoint endpoint = GetEndpoint(uri);
			Socket socket = null;
			_Sockets.TryGetValue(endpoint, out socket);
			return socket;
		}
		private void Disconnect(Socket socket)
		{
			Socket existing = null;
			if(_Sockets.TryRemove((IPEndPoint)socket.RemoteEndPoint, out existing))
			{
				if(socket != existing)
					throw new InvalidOperationException("Bug in TCPHttpMessageHandler, contact developers");
				socket.AsSafeDisposable().Dispose();
			}
		}

		private async Task<Socket> EnsureConnectedAsync(Uri request, CancellationToken cancellationToken)
		{
			var endpoint = GetEndpoint(request);
			bool createdSocket = false;
			var socket = _Sockets.GetOrAdd(endpoint, _ =>
			{
				var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				createdSocket = true;
				return s;
			});
			if(createdSocket)
			{
				using(var disposables = new CompositeDisposable())
				{
					CancellationTokenSource connectTimeout = new CancellationTokenSource();
					connectTimeout.CancelAfter(Options.ConnectTimeout);
					var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectTimeout.Token);
					await socket.ConnectAsync(endpoint, linked.Token).ConfigureAwait(false);
				}
			}
			return socket;
		}

		private static IPEndPoint GetEndpoint(Uri request)
		{
			return new IPEndPoint(IPAddress.Parse(request.Host), request.Port);
		}

		private async Task<HttpResponseMessage> GetResponseAsync(TCPStream tcpStream)
		{
			var response = new HttpResponseMessage()
			{
				StatusCode = (HttpStatusCode)await tcpStream.ReadVarIntAsync().ConfigureAwait(false)
			};

			List<Tuple<string, string>> headers = new List<Tuple<string, string>>();
			if(Options.IncludeHeaders)
			{
				var headersCount = await tcpStream.ReadVarIntAsync().ConfigureAwait(false);
				for(int i = 0; i < (int)headersCount; i++)
				{
					var key = await tcpStream.ReadStringAync().ConfigureAwait(false);
					var value = await tcpStream.ReadStringAync().ConfigureAwait(false);
					headers.Add(Tuple.Create(key, value));
				}
			}

			var headersByKey = headers.GroupBy(h => h.Item1, h => h.Item2);
			var length = await tcpStream.ReadVarIntAsync().ConfigureAwait(false);
			if((int)length > tcpStream.MaxArrayLength)
				throw new IOException("Server's response is too long");
			var bodyArray = new byte[(int)length];
			if(length != 0)
			{
				int readen = 0;
				while(readen != bodyArray.Length)
					readen += await tcpStream.Inner.ReadAsync(bodyArray, readen, bodyArray.Length - readen, tcpStream.Cancellation).ConfigureAwait(false);
			}
			response.Content = new StreamContent(new MemoryStream(bodyArray));
			response.Content.Headers.ContentLength = (int)length;
			if(Options.IncludeHeaders)
			{
				foreach(var g in headersByKey)
				{
					if(response.Content.Headers.Contains(g.Key))
						response.Content.Headers.Remove(g.Key);
					response.Content.Headers.TryAddWithoutValidation(g.Key, g);
				}
			}

			return response;
		}

		bool _Disposed;
		protected override void Dispose(bool disposing)
		{
			AssertNotDisposed();
			foreach(var socket in _Sockets.Values)
			{
				Disconnect(socket);
			}
			_Disposed = true;
			base.Dispose(disposing);
		}

		private void AssertNotDisposed()
		{
			if(_Disposed)
				throw new ObjectDisposedException("TCPHttpMessageHandler");
		}
	}
}
