using System;
using Rewired;
using UnityEngine;

// MOD ONLINE: ponto unico por onde o jogo le os controles. Quando este PC e o host
// e tem um amigo conectado, o player 2 (Rewired id 1) recebe tambem os comandos que
// chegam pela rede.
public static class NetInput
{
	public const int ButtonCount = 32;

	public static readonly int[] SentAxes = new int[4] { 0, 1, 22, 23 };

	// Acoes de botao existentes em CupheadButton
	public static readonly int[] SentButtons = new int[17] { 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 16, 18, 19, 20, 26 };

	private static readonly object sync = new object();

	private static uint latestButtons;

	private static readonly float[] latestAxes = new float[4];

	private static uint curButtons;

	private static uint prevButtons;

	private static readonly float[] curAxes = new float[4];

	private static readonly float[] prevAxes = new float[4];

	private static int lastFrame = -1;

	public static bool RemoteActive { get; private set; }

	public static uint LatestButtons
	{
		get
		{
			lock (sync)
			{
				return latestButtons;
			}
		}
	}

	public static void SetRemoteActive(bool active)
	{
		lock (sync)
		{
			RemoteActive = active;
			latestButtons = 0u;
			Array.Clear(latestAxes, 0, latestAxes.Length);
		}
	}

	public static void SetRemoteActiveIfChanged(bool active)
	{
		if (RemoteActive != active)
		{
			SetRemoteActive(active);
		}
	}

	// Chamado pela thread de rede a cada pacote do amigo
	public static void ReceiveRemote(uint buttons, float[] axes)
	{
		lock (sync)
		{
			latestButtons = buttons;
			for (int i = 0; i < latestAxes.Length; i++)
			{
				latestAxes[i] = axes[i];
			}
		}
	}

	// Lockstep (NetcodeSync): os dois PCs usam exatamente os mesmos controles por frame.
	// Player 1 = host, player 2 = amigo, nos dois jogos; o controle fisico nao e lido direto.
	public static bool LockstepActive { get; private set; }

	private static readonly NetProtocol.InputFrame[] lsCur = new NetProtocol.InputFrame[2];

	private static readonly NetProtocol.InputFrame[] lsPrev = new NetProtocol.InputFrame[2];

	public static void SetLockstepActive(bool active)
	{
		LockstepActive = active;
		lsCur[0] = (lsCur[1] = (lsPrev[0] = (lsPrev[1] = default(NetProtocol.InputFrame))));
	}

	public static void SetLockstepFrame(NetProtocol.InputFrame p1, NetProtocol.InputFrame p2)
	{
		lsPrev[0] = lsCur[0];
		lsPrev[1] = lsCur[1];
		lsCur[0] = p1;
		lsCur[1] = p2;
	}

	private static bool IsLockstep(Player p)
	{
		if (!LockstepActive || p == null || p.id < 0 || p.id > 1)
		{
			return false;
		}
		NetcodeSync.Advance();
		return LockstepActive;
	}

	private static float LsAxis(NetProtocol.InputFrame f, int action)
	{
		int slot = AxisSlot(action);
		return (slot >= 0) ? f.Axis(slot) : 0f;
	}

	private static bool IsRemote(Player p)
	{
		if (!RemoteActive || p == null || p.id != 1)
		{
			return false;
		}
		int frameCount = Time.frameCount;
		if (frameCount != lastFrame)
		{
			lastFrame = frameCount;
			lock (sync)
			{
				prevButtons = curButtons;
				curButtons = latestButtons;
				for (int i = 0; i < curAxes.Length; i++)
				{
					prevAxes[i] = curAxes[i];
					curAxes[i] = latestAxes[i];
				}
			}
		}
		return true;
	}

	private static bool Bit(uint mask, int action)
	{
		return action >= 0 && action < ButtonCount && (mask & (uint)(1 << action)) != 0;
	}

	private static int AxisSlot(int action)
	{
		for (int i = 0; i < SentAxes.Length; i++)
		{
			if (SentAxes[i] == action)
			{
				return i;
			}
		}
		return -1;
	}

	public static bool GetButton(Player p, int action)
	{
		if (IsLockstep(p))
		{
			return Bit(lsCur[p.id].buttons, action);
		}
		return p.GetButton(action) || (IsRemote(p) && Bit(curButtons, action));
	}

	public static bool GetButtonDown(Player p, int action)
	{
		if (IsLockstep(p))
		{
			return Bit(lsCur[p.id].buttons, action) && !Bit(lsPrev[p.id].buttons, action);
		}
		return p.GetButtonDown(action) || (IsRemote(p) && Bit(curButtons, action) && !Bit(prevButtons, action));
	}

	public static bool GetButtonUp(Player p, int action)
	{
		if (IsLockstep(p))
		{
			return !Bit(lsCur[p.id].buttons, action) && Bit(lsPrev[p.id].buttons, action);
		}
		return p.GetButtonUp(action) || (IsRemote(p) && !Bit(curButtons, action) && Bit(prevButtons, action));
	}

	public static bool GetNegativeButtonDown(Player p, int action)
	{
		if (IsLockstep(p))
		{
			return LsAxis(lsCur[p.id], action) < -0.5f && LsAxis(lsPrev[p.id], action) >= -0.5f;
		}
		if (p.GetNegativeButtonDown(action))
		{
			return true;
		}
		int slot = AxisSlot(action);
		return slot >= 0 && IsRemote(p) && curAxes[slot] < -0.5f && prevAxes[slot] >= -0.5f;
	}

	public static float GetAxis(Player p, int action)
	{
		if (IsLockstep(p))
		{
			return LsAxis(lsCur[p.id], action);
		}
		float axis = p.GetAxis(action);
		int slot = AxisSlot(action);
		if (slot >= 0 && IsRemote(p) && Mathf.Abs(curAxes[slot]) > Mathf.Abs(axis))
		{
			return curAxes[slot];
		}
		return axis;
	}

	public static float GetAxisRaw(Player p, int action)
	{
		if (IsLockstep(p))
		{
			return LsAxis(lsCur[p.id], action);
		}
		float axisRaw = p.GetAxisRaw(action);
		int slot = AxisSlot(action);
		if (slot >= 0 && IsRemote(p) && Mathf.Abs(curAxes[slot]) > Mathf.Abs(axisRaw))
		{
			return curAxes[slot];
		}
		return axisRaw;
	}

	public static bool GetAnyButtonDown(Player p)
	{
		if (IsLockstep(p))
		{
			return (lsCur[p.id].buttons & ~lsPrev[p.id].buttons) != 0;
		}
		return p.GetAnyButtonDown() || (IsRemote(p) && (curButtons & ~prevButtons) != 0);
	}

	public static bool GetAnyButton(Player p)
	{
		if (IsLockstep(p))
		{
			return lsCur[p.id].buttons != 0;
		}
		return p.GetAnyButton() || (IsRemote(p) && curButtons != 0);
	}

	// Repasses para os outros tipos que tambem tem metodos com esses nomes
	public static bool GetButton(Player p, string action) => p.GetButton(action);

	public static bool GetButtonDown(Player p, string action) => p.GetButtonDown(action);

	public static bool GetButtonUp(Player p, string action) => p.GetButtonUp(action);

	public static float GetAxis(Player p, string action) => p.GetAxis(action);

	public static bool GetButton(CupheadInput.AnyPlayerInput i, CupheadButton b) => i.GetButton(b);

	public static bool GetButtonDown(CupheadInput.AnyPlayerInput i, CupheadButton b) => i.GetButtonDown(b);

	public static bool GetButtonUp(CupheadInput.AnyPlayerInput i, CupheadButton b) => i.GetButtonUp(b);

	public static bool GetAnyButtonDown(CupheadInput.AnyPlayerInput i) => i.GetAnyButtonDown();

	public static float GetAxis(PlayerInput i, PlayerInput.Axis a) => i.GetAxis(a);

	public static bool GetButton(PlayerInput i, CupheadButton b) => i.GetButton(b);

	// Leitura local (lado do amigo): estado do controle deste PC para mandar ao host
	public static void ReadLocal(Player p, out uint buttons, float[] axes)
	{
		buttons = 0u;
		foreach (int i in SentButtons)
		{
			if (p.GetButton(i))
			{
				buttons |= (uint)(1 << i);
			}
		}
		for (int j = 0; j < SentAxes.Length; j++)
		{
			axes[j] = p.GetAxis(SentAxes[j]);
		}
	}
}
