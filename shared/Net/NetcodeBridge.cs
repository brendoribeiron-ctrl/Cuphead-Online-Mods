using System;
using UnityEngine;

// MOD ONLINE: liga o NetSession (lobby) ao NetcodeSync (lockstep) no modo Sync.
// Pacotes 0x20-0x3F vao direto da thread de leitura para NetcodeSync.OnPacket.
public class NetcodeBridge : MonoBehaviour
{
	private class SessionTransport : INetcodeTransport
	{
		public bool Connected
		{
			get
			{
				NetSession s = NetSession.Instance;
				return s != null && s.PeerConnected;
			}
		}

		public void Send(byte type, byte[] payload)
		{
			NetSession s = NetSession.Instance;
			if (s != null)
			{
				s.Send(type, payload);
			}
		}
	}

	private NetSession session;

	private readonly SessionTransport transport = new SessionTransport();

	private bool running;

	private void Start()
	{
		session = NetSession.Instance;
		for (int i = NetProtocol.FirstType; i <= NetProtocol.LastType; i++)
		{
			byte type = (byte)i;
			session.RegisterHandler(type, delegate(int from, byte[] payload)
			{
				if (running)
				{
					NetcodeSync.OnPacket(type, payload);
				}
			}, onNetworkThread: true);
		}
		session.GameStarted += delegate
		{
			Begin();
		};
		session.PlayerReconnected += delegate
		{
			if (session.State == NetSession.SessionState.InGame)
			{
				Begin();
			}
		};
		session.PlayerDisconnected += delegate
		{
			End();
		};
		session.PlayerLeft += delegate
		{
			End();
		};
		session.SessionEnded += delegate
		{
			End();
		};
	}

	private void Begin()
	{
		if (running || session.Mode != NetSession.GameMode.Sync)
		{
			return;
		}
		running = true;
		NetLog.Info("netcode sincronizado iniciado (host=" + session.IsHost + ")");
		try
		{
			NetcodeSync.Begin(transport, session.IsHost);
		}
		catch (Exception ex)
		{
			NetLog.Info("ERRO " + ex);
		}
	}

	private void End()
	{
		if (!running)
		{
			return;
		}
		running = false;
		try
		{
			NetcodeSync.End();
		}
		catch (Exception ex)
		{
			NetLog.Info("ERRO " + ex);
		}
	}
}
