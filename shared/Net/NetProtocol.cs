using System;
using System.IO;
using System.Text;

// MOD ONLINE: protocolo do netcode (cada PC roda o proprio jogo em lockstep).
//
// Transporte: o NetSession do lobby (branch net/lobby) entrega mensagens
// [tipo:byte][len:int32][payload]. Tipos 0x00-0x1F sao do lobby; o netcode usa 0x20-0x3F.
// O lobby so precisa:
//   1) quando a partida comecar: NetcodeSync.Begin(transport, isHost)
//      (transport implementa INetcodeTransport, normalmente um adaptador do NetSession)
//   2) para todo pacote com tipo 0x20..0x3F: NetcodeSync.OnPacket(tipo, payload)
//      PODE ser chamado da thread de rede (e o ideal: o lockstep espera input no meio do frame)
//   3) quando a conexao cair: NetcodeSync.End()
//
// Ideia do lockstep: os dois jogos rodam com deltaTime fixo (1/60), mesma semente de
// Random e avancam frame a frame so quando tem o input dos dois jogadores daquele frame.
// Host = player 1, amigo = player 2 nos dois PCs. O input local e aplicado com atraso de
// InputDelay frames para esconder a latencia. O host decide as trocas de cena; ha uma
// barreira na troca de cena e checksums periodicos com correcao vinda do host.
public interface INetcodeTransport
{
	bool Connected { get; }

	void Send(byte type, byte[] payload);
}

public static class NetProtocol
{
	public const ushort Version = 5;

	public const byte FirstType = 0x20;

	public const byte LastType = 0x3F;

	// Netcode -> netcode. Todos os campos little-endian (BinaryWriter).

	// Hello: u16 versao, i32 hash da build (NetProtocol.BuildHash). Os dois mandam ao comecar.
	public const byte Hello = 0x20;

	// Config (host -> amigo): u8 numero da partida, u8 inputDelay, string save do host (JSON do PlayerData), i32 Levels atual.
	public const byte Config = 0x21;

	// Input: u8 numero da partida (do Config), i32 primeiro frame, u8 quantidade N, N x InputFrame (frames seguidos a partir do primeiro).
	public const byte Input = 0x22;

	// InputAck: reservado (o TCP ja garante entrega e ordem).
	public const byte InputAck = 0x23;

	// SceneLoad (host -> amigo): string cena, i32 Levels, u8 transicao inicio, u8 transicao fim, u8 icone.
	public const byte SceneLoad = 0x24;

	// SceneReady (amigo -> host): u8 etapa (0 = antes de ativar a cena, 1 = depois de carregar tudo), string cena.
	public const byte SceneReady = 0x25;

	// SceneGo (host -> amigo): u8 etapa, string cena, i32 frame alvo, i32 seed, string save do host (json,
	// vazio fora da etapa 0). Os dois seguem no mesmo frame; o amigo troca o save pelo do host.
	public const byte SceneGo = 0x26;

	// Checksum: reservado (o hash vai dentro do Snapshot).
	public const byte Checksum = 0x27;

	// Snapshot (host -> amigo): i32 frame, u32 hash, u8 nPlayers, n x PlayerState, f32 dano no chefe (-1 = sem chefe).
	public const byte Snapshot = 0x28;

	// Pause: u8 pausado, i32 frame. Quem pausa avisa; o outro pausa junto.
	public const byte Pause = 0x29;

	// Ping/Pong: i64 ticks do relogio de quem mandou (eco no Pong). Mede latencia para o InputDelay.
	public const byte Ping = 0x2A;

	public const byte Pong = 0x2B;

	// Bye: string motivo. Saida limpa do lockstep (volta a jogar sozinho).
	public const byte Bye = 0x2C;

	// Resync (amigo -> host): string motivo. O jogo do amigo se separou de um jeito que nao da para
	// corrigir (alguem morto so de um lado, player sumido): o host recarrega a cena atual para os dois.
	public const byte Resync = 0x2D;

	// TraceDump (amigo -> host): i32 frame. O amigo viu a primeira dessincronia da cena; o host grava
	// no log o rastro dos ultimos frames para comparar com o do amigo.
	public const byte TraceDump = 0x2E;

	public const int DefaultInputDelay = 3;

	public static int BuildHash => typeof(NetProtocol).Assembly.ManifestModule.ModuleVersionId.GetHashCode();

	public static bool IsNetcodeType(byte type)
	{
		return type >= FirstType && type <= LastType;
	}

	// Estado dos controles de um jogador em um frame (mesmo layout do NetInput).
	public struct InputFrame
	{
		public uint buttons;

		// eixos quantizados -127..127 (NetInput.SentAxes)
		public sbyte ax0;

		public sbyte ax1;

		public sbyte ax2;

		public sbyte ax3;

		public float Axis(int i)
		{
			switch (i)
			{
			case 0:
				return (float)ax0 / 127f;
			case 1:
				return (float)ax1 / 127f;
			case 2:
				return (float)ax2 / 127f;
			default:
				return (float)ax3 / 127f;
			}
		}

		public static sbyte Quantize(float v)
		{
			return (sbyte)Math.Round(Math.Max(-1f, Math.Min(1f, v)) * 127f);
		}

		public static InputFrame From(uint buttons, float[] axes)
		{
			InputFrame result = default(InputFrame);
			result.buttons = buttons;
			result.ax0 = Quantize(axes[0]);
			result.ax1 = Quantize(axes[1]);
			result.ax2 = Quantize(axes[2]);
			result.ax3 = Quantize(axes[3]);
			return result;
		}

		public void Write(BinaryWriter w)
		{
			w.Write(buttons);
			w.Write(ax0);
			w.Write(ax1);
			w.Write(ax2);
			w.Write(ax3);
		}

		public static InputFrame Read(BinaryReader r)
		{
			InputFrame result = default(InputFrame);
			result.buttons = r.ReadUInt32();
			result.ax0 = r.ReadSByte();
			result.ax1 = r.ReadSByte();
			result.ax2 = r.ReadSByte();
			result.ax3 = r.ReadSByte();
			return result;
		}
	}

	public struct PlayerState
	{
		public byte id;

		public bool present;

		public float x;

		public float y;

		public int hp;

		public float super;

		public void Write(BinaryWriter w)
		{
			w.Write(id);
			w.Write(present);
			w.Write(x);
			w.Write(y);
			w.Write(hp);
			w.Write(super);
		}

		public static PlayerState Read(BinaryReader r)
		{
			PlayerState result = default(PlayerState);
			result.id = r.ReadByte();
			result.present = r.ReadBoolean();
			result.x = r.ReadSingle();
			result.y = r.ReadSingle();
			result.hp = r.ReadInt32();
			result.super = r.ReadSingle();
			return result;
		}
	}

	public static byte[] Build(Action<BinaryWriter> write)
	{
		using (MemoryStream memoryStream = new MemoryStream())
		{
			BinaryWriter binaryWriter = new BinaryWriter(memoryStream, Encoding.UTF8);
			write(binaryWriter);
			binaryWriter.Flush();
			return memoryStream.ToArray();
		}
	}

	public static BinaryReader Reader(byte[] payload)
	{
		return new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
	}
}
