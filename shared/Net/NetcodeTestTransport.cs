using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

// MOD ONLINE: transporte TCP minimo so para testar o netcode sem o lobby.
// Mesmo formato de mensagem do NetSession: [tipo:byte][len:int32][payload].
//   Cuphead.exe -netcode-host [-netcode-port 7778]
//   Cuphead.exe -netcode-join 127.0.0.1 [-netcode-port 7778]
// Na partida de verdade quem chama NetcodeSync.Begin e o lobby (NetSession).
public class NetcodeTestTransport : INetcodeTransport
{
	public const int DefaultPort = 7778;

	private const int MaxPayload = 8388608;

	private readonly object writeLock = new object();

	private TcpClient client;

	private BinaryWriter writer;

	private volatile bool connected;

	public bool Connected => connected;

	// Teste rodando: o jogo nao pode pausar sem foco (senao quem espera a conexao congela para sempre)
	public static bool Started { get; private set; }

	public static void StartFromCommandLine()
	{
		string[] commandLineArgs = Environment.GetCommandLineArgs();
		int port = DefaultPort;
		string host = null;
		bool flag = false;
		for (int i = 0; i < commandLineArgs.Length; i++)
		{
			if (commandLineArgs[i] == "-netcode-host")
			{
				flag = true;
			}
			else if (commandLineArgs[i] == "-netcode-join" && i + 1 < commandLineArgs.Length)
			{
				host = commandLineArgs[i + 1];
			}
			else if (commandLineArgs[i] == "-netcode-port" && i + 1 < commandLineArgs.Length)
			{
				int.TryParse(commandLineArgs[i + 1], out port);
			}
		}
		Started = flag || host != null;
		if (Started)
		{
			Application.runInBackground = true;
		}
		if (flag)
		{
			new NetcodeTestTransport().Listen(port);
		}
		else if (host != null)
		{
			new NetcodeTestTransport().Connect(host, port);
		}
	}

	private void Listen(int port)
	{
		Thread thread = new Thread((ThreadStart)delegate
		{
			try
			{
				TcpListener tcpListener = new TcpListener(IPAddress.Any, port);
				tcpListener.Start();
				NetLog.Info("[NetcodeTest] esperando conexao na porta " + port);
				TcpClient tcp = tcpListener.AcceptTcpClient();
				tcpListener.Stop();
				Run(tcp, isHost: true);
			}
			catch (Exception ex)
			{
				NetLog.Info("[NetcodeTest] host falhou: " + ex.Message);
			}
		});
		thread.IsBackground = true;
		thread.Start();
	}

	private void Connect(string host, int port)
	{
		Thread thread = new Thread((ThreadStart)delegate
		{
			for (int i = 0; i < 60; i++)
			{
				try
				{
					TcpClient tcpClient = new TcpClient();
					tcpClient.Connect(host, port);
					Run(tcpClient, isHost: false);
					return;
				}
				catch (Exception ex)
				{
					NetLog.Info("[NetcodeTest] tentando conectar: " + ex.Message);
					Thread.Sleep(1000);
				}
			}
		});
		thread.IsBackground = true;
		thread.Start();
	}

	private void Run(TcpClient tcp, bool isHost)
	{
		tcp.NoDelay = true;
		client = tcp;
		NetworkStream stream = tcp.GetStream();
		writer = new BinaryWriter(stream);
		connected = true;
		NetLog.Info("[NetcodeTest] conectado (" + (isHost ? "host" : "amigo") + ")");
		NetcodeSync.Begin(this, isHost);
		try
		{
			BinaryReader binaryReader = new BinaryReader(stream);
			while (connected)
			{
				byte type = binaryReader.ReadByte();
				int num = binaryReader.ReadInt32();
				if (num < 0 || num > MaxPayload)
				{
					throw new IOException("tamanho invalido");
				}
				byte[] payload = binaryReader.ReadBytes(num);
				if (NetProtocol.IsNetcodeType(type))
				{
					NetcodeSync.OnPacket(type, payload);
				}
			}
		}
		catch (Exception ex)
		{
			NetLog.Info("[NetcodeTest] conexao encerrada: " + ex.Message);
		}
		connected = false;
		try
		{
			tcp.Close();
		}
		catch (Exception)
		{
		}
		NetcodeSync.End("conexao caiu");
	}

	public void Send(byte type, byte[] payload)
	{
		lock (writeLock)
		{
			if (!connected)
			{
				return;
			}
			try
			{
				writer.Write(type);
				writer.Write(payload.Length);
				writer.Write(payload);
				writer.Flush();
			}
			catch (Exception)
			{
				connected = false;
			}
		}
	}
}
