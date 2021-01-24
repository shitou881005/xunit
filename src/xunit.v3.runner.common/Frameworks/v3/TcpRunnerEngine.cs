﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Internal;
using Xunit.v3;

namespace Xunit.Runner.v3
{
	/// <summary>
	/// The runner-side engine used to host an xUnit.net v3 test assembly. Opens a port
	/// for message communication, and translates the communication channel back into v3
	/// message objects which are passed to the provided <see cref="_IMessageSink"/>.
	/// Sends commands to the remote side, which is running <see cref="T:Xunit.Runner.v3.TcpExecutionEngine"/>.
	/// </summary>
	public class TcpRunnerEngine : TcpEngine, IAsyncDisposable
	{
		int cancelRequested = 0;
		BufferedTcpClient? bufferedClient;
		readonly List<Action> cleanupTasks = new();
		readonly _IMessageSink messageSink;
		bool quitSent = false;

		/// <summary>
		/// Initializes a new instance of the <see cref="TcpRunnerEngine"/> class.
		/// </summary>
		/// <param name="engineID">Engine ID (used for diagnostic messages).</param>
		/// <param name="messageSink">The message sink to pass remote messages to.</param>
		/// <param name="diagnosticMessageSink">The message sink to send diagnostic messages to.</param>
		public TcpRunnerEngine(
			string engineID,
			_IMessageSink messageSink,
			_IMessageSink diagnosticMessageSink) :
				base(engineID, diagnosticMessageSink)
		{
			AddCommandHandler(TcpEngineMessages.Execution.Info, OnInfo);
			AddCommandHandler(TcpEngineMessages.Execution.Message, OnMessage);

			this.messageSink = Guard.ArgumentNotNull(nameof(messageSink), messageSink);
		}

		/// <summary>
		/// Gets the <see cref="TcpExecutionEngineInfo"/> received during protocol negotiation. Will
		/// be <c>null</c> unless <see cref="TcpEngine.State"/> is <see cref="TcpEngineState.Connected"/>.
		/// </summary>
		public TcpExecutionEngineInfo? ExecutionEngineInfo { get; private set; }

		/// <inheritdoc/>
		protected async override ValueTask Disconnect()
		{
			if (bufferedClient != null)
			{
				if (!quitSent)
					SendQuit();

				await bufferedClient.DisposeAsync();

				bufferedClient = null;
			}

			foreach (var cleanupTask in cleanupTasks)
			{
				try
				{
					cleanupTask();
				}
				catch (Exception ex)
				{
					DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): Error during TCP server cleanup: {ex}" });
				}
			}
		}

		void OnInfo(ReadOnlyMemory<byte>? data)
		{
			if (!data.HasValue)
			{
				DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): INFO data is missing the JSON" });
				return;
			}

			ExecutionEngineInfo = JsonSerializer.Deserialize<TcpExecutionEngineInfo>(data.Value.Span);

			lock (StateLock)
			{
				if (State != TcpEngineState.Negotiating)
					DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): INFO message received before we reached {TcpEngineState.Negotiating} state (current state is {State})" });
				else
					State = TcpEngineState.Connected;
			}
		}

		void OnMessage(ReadOnlyMemory<byte>? data)
		{
			if (!data.HasValue)
			{
				DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): MSG data is missing the operation ID and JSON" });
				return;
			}

			var (requestID, json) = TcpEngineMessages.SplitOnSeparator(data.Value);

			if (!json.HasValue)
			{
				DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): MSG data is missing the JSON" });
				return;
			}

			var deserializedMessage = _MessageSinkMessage.ParseJson(json.Value);
			var @continue = messageSink.OnMessage(deserializedMessage);

			if (!@continue && Interlocked.Exchange(ref cancelRequested, 1) == 0)
			{
				bufferedClient?.Send(TcpEngineMessages.Runner.Cancel);
				bufferedClient?.Send(TcpEngineMessages.EndOfMessage);
			}

			if (State != TcpEngineState.Connected)
				DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): MSG message received before we reached {TcpEngineState.Connected} state (current state is {State})" });
		}

		/// <summary>
		/// Sends <see cref="TcpEngineMessages.Runner.Cancel"/>.
		/// </summary>
		/// <param name="operationID">The operation ID to cancel.</param>
		public void SendCancel(string operationID)
		{
			Guard.ArgumentNotNull(nameof(operationID), operationID);

			if (bufferedClient == null)
			{
				DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): {nameof(SendCancel)} called when there is no connected execution engine" });
				return;
			}

			bufferedClient.Send(TcpEngineMessages.Runner.Cancel);
			bufferedClient.Send(TcpEngineMessages.Separator);
			bufferedClient.Send(operationID);
			bufferedClient.Send(TcpEngineMessages.EndOfMessage);
		}

		/// <summary>
		/// Sends <see cref="TcpEngineMessages.Runner.Quit"/>.
		/// </summary>
		public void SendQuit()
		{
			if (bufferedClient == null)
			{
				DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): {nameof(SendQuit)} called when there is no connected execution engine" });
				return;
			}

			quitSent = true;

			bufferedClient.Send(TcpEngineMessages.Runner.Quit);
			bufferedClient.Send(TcpEngineMessages.EndOfMessage);
		}

		/// <summary>
		/// Start the TCP server. Stop the server by disposing it.
		/// </summary>
		/// <returns>Returns the TCP port that the server is listening on.</returns>
		public int Start()
		{
			int listenPort;
			Socket listenSocket;

			lock (StateLock)
			{
				if (State != TcpEngineState.Initialized)
					throw new InvalidOperationException($"Cannot call {nameof(Start)} on {nameof(TcpRunnerEngine)} in any state other than {TcpEngineState.Initialized} (currently in state {State})");

				listenSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
				cleanupTasks.Add(() =>
				{
					listenSocket.Close();
					listenSocket.Dispose();
				});

				listenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
				listenSocket.Listen(1);

				listenPort = ((IPEndPoint)listenSocket.LocalEndPoint!).Port;

				State = TcpEngineState.Listening;
			}

			Task.Run(async () =>
			{
				var socket = await listenSocket.AcceptAsync();
				listenSocket.Close();

				var remotePort = ((IPEndPoint?)socket.RemoteEndPoint)?.Port.ToString() ?? "<unknown_port>";

				DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): Connection accepted from tcp://localhost:{remotePort}/" });

				cleanupTasks.Add(() =>
				{
					DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): Disconnecting from tcp://localhost:{remotePort}/" });

					socket.Shutdown(SocketShutdown.Receive);
					socket.Shutdown(SocketShutdown.Send);
					socket.Close();
					socket.Dispose();

					DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): Disconnected from tcp://localhost:{remotePort}/" });
				});

				bufferedClient = new BufferedTcpClient($"runner::{EngineID}", socket, ProcessRequest, DiagnosticMessageSink);
				bufferedClient.Start();

				// Send INFO message to start protocol negotiation
				lock (StateLock)
					State = TcpEngineState.Negotiating;

				var engineInfo = new TcpRunnerEngineInfo();

				bufferedClient.Send(TcpEngineMessages.Runner.Info);
				bufferedClient.Send(TcpEngineMessages.Separator);
				bufferedClient.Send(JsonSerializer.Serialize(engineInfo));
				bufferedClient.Send(TcpEngineMessages.EndOfMessage);
			});

			DiagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"{nameof(TcpRunnerEngine)}({EngineID}): Listening on tcp://localhost:{listenPort}/" });

			return listenPort;
		}
	}
}