using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;

namespace NetLab;

// Protocolo do lobby da main (commit cda67db): o host transmite a tela, o amigo manda controles.
//   1 = quadro : i32 tamanho + JPEG          (host -> amigo)
//   2 = input  : u32 botoes + 4 x f32 eixos   (amigo -> host)
public static class Legacy
{
	public const byte PacketFrame = 1;

	public const byte PacketInput = 2;

	// Nomes das acoes de CupheadButton que o NetInput manda
	public static readonly Dictionary<int, string> ButtonNames = new()
	{
		[2] = "Jump", [3] = "Shoot", [4] = "Super", [5] = "SwitchWeapon", [6] = "Lock", [7] = "Dash",
		[8] = "Pause", [11] = "NextPage", [12] = "PreviousPage", [13] = "Accept", [14] = "Cancel", [15] = "EquipMenu",
		[16] = "MenuUp", [18] = "MenuLeft", [19] = "MenuDown", [20] = "MenuRight", [26] = "Swap"
	};

	public static string Describe(uint buttons)
	{
		if (buttons == 0)
		{
			return "-";
		}
		var names = new List<string>();
		for (int i = 0; i < 32; i++)
		{
			if ((buttons & (1u << i)) != 0)
			{
				names.Add(ButtonNames.TryGetValue(i, out var n) ? n : i.ToString());
			}
		}
		return string.Join("+", names);
	}

	// Roteiro de controles do cliente simulado: (botoes, eixoX, eixoY) em funcao do tempo
	public static (uint buttons, float x, float y) Script(string name, double t)
	{
		switch (name)
		{
		case "idle":
			return (0, 0, 0);
		case "walk":   // anda 1s para a direita, 1s para a esquerda
			return (0, (int)(t) % 2 == 0 ? 1f : -1f, 0);
		case "jump":   // pula a cada 0.5s
			return ((t % 0.5) < 0.1 ? 1u << 2 : 0, 0, 0);
		case "shoot":  // anda e atira
			return (1u << 3, (int)(t / 2) % 2 == 0 ? 1f : -1f, 0);
		case "join":   // aperta Aceitar de vez em quando (entra como player 2 no mapa)
			return ((t % 1.0) < 0.1 ? 1u << 13 : 0, 0, 0);
		default:       // mash: tudo
			int step = (int)(t * 10);
			uint b = (uint)(1 << new[] { 2, 3, 7, 4, 6 }[step % 5]);
			return (b, (step / 5) % 2 == 0 ? 1f : -1f, (step / 7) % 2 == 0 ? 0f : 1f);
		}
	}

	// ---------- Amigo simulado (conecta num host de verdade) ----------

	public static int RunClient(Args a)
	{
		IPEndPoint ep = Args.ParseEndPoint(a.Str("connect", "127.0.0.1:7777"), 7777);
		string script = a.Str("script", "walk");
		double seconds = a.Num("seconds", 20);
		double hz = a.Num("hz", 60);
		string saveDir = a.Str("save-frames", null);
		var r = ClientSession(ep, script, seconds, hz, saveDir);
		Log.Info("cliente", r.ToString());
		return r.Ok ? 0 : 1;
	}

	public sealed class ClientResult
	{
		public bool Ok;

		public string Error;

		public int Frames;

		public int BadFrames;

		public long FrameBytes;

		public double Seconds;

		public double MaxGapMs;

		public int InputsSent;

		public override string ToString() => Ok
			? $"OK: {Frames} quadros em {Seconds:0.0}s ({Frames / Math.Max(0.001, Seconds):0.0} fps), {FrameBytes / Math.Max(0.001, Seconds) / 1024:0} KB/s, maior buraco {MaxGapMs:0} ms, {BadFrames} quadros invalidos, {InputsSent} inputs enviados"
			: "FALHOU: " + Error;
	}

	public static ClientResult ClientSession(IPEndPoint ep, string script, double seconds, double hz, string saveDir = null)
	{
		var res = new ClientResult();
		using var tcp = new TcpClient { NoDelay = true };
		try
		{
			tcp.Connect(ep);
		}
		catch (Exception ex)
		{
			res.Error = "nao conectou em " + ep + ": " + ex.Message;
			return res;
		}
		Log.Info("cliente", "conectado em " + ep + ", roteiro " + script);
		NetworkStream ns = tcp.GetStream();
		var sw = Stopwatch.StartNew();
		bool stop = false;
		string readError = null;
		double lastFrameMs = -1;
		var reader = new Thread(() =>
		{
			try
			{
				var br = new BinaryReader(ns);
				while (!stop)
				{
					byte type = br.ReadByte();
					if (type != PacketFrame)
					{
						throw new IOException("tipo inesperado do host: " + type);
					}
					int len = br.ReadInt32();
					if (len <= 0 || len > 8 << 20)
					{
						throw new IOException("tamanho de quadro invalido: " + len);
					}
					byte[] jpg = br.ReadBytes(len);
					if (jpg.Length != len)
					{
						throw new IOException("conexao fechou no meio do quadro");
					}
					double now = sw.Elapsed.TotalMilliseconds;
					lock (res)
					{
						if (lastFrameMs >= 0)
						{
							res.MaxGapMs = Math.Max(res.MaxGapMs, now - lastFrameMs);
						}
						lastFrameMs = now;
						res.Frames++;
						res.FrameBytes += len;
						if (jpg.Length < 4 || jpg[0] != 0xFF || jpg[1] != 0xD8 || jpg[^2] != 0xFF || jpg[^1] != 0xD9)
						{
							res.BadFrames++;
						}
					}
					if (saveDir != null && res.Frames % 20 == 1)
					{
						Directory.CreateDirectory(saveDir);
						File.WriteAllBytes(Path.Combine(saveDir, $"quadro_{res.Frames:00000}.jpg"), jpg);
					}
				}
			}
			catch (Exception ex)
			{
				if (!stop)
				{
					readError = ex.Message;
				}
			}
		}) { IsBackground = true };
		reader.Start();
		var bw = new BinaryWriter(ns);
		uint lastButtons = uint.MaxValue;
		try
		{
			while (sw.Elapsed.TotalSeconds < seconds && readError == null)
			{
				var (b, x, y) = Script(script, sw.Elapsed.TotalSeconds);
				bw.Write(PacketInput);
				bw.Write(b);
				bw.Write(x);
				bw.Write(y);
				bw.Write(0f);
				bw.Write(0f);
				bw.Flush();
				res.InputsSent++;
				if (b != lastButtons)
				{
					lastButtons = b;
				}
				Thread.Sleep((int)(1000 / hz));
			}
		}
		catch (Exception ex)
		{
			readError ??= "envio: " + ex.Message;
		}
		stop = true;
		res.Seconds = sw.Elapsed.TotalSeconds;
		if (readError != null)
		{
			res.Error = readError + " (depois de " + res.Seconds.ToString("0.0") + "s, " + res.Frames + " quadros)";
			return res;
		}
		if (res.Frames == 0)
		{
			res.Error = "conectou mas nao recebeu nenhum quadro da tela";
			return res;
		}
		res.Ok = true;
		return res;
	}

	// ---------- Host simulado (para testar o lado do amigo no jogo) ----------

	public static int RunHost(Args a)
	{
		int port = a.Int("port", 7777);
		double fps = a.Num("fps", 20);
		double seconds = a.Num("seconds", 0);
		var listener = new TcpListener(IPAddress.Any, port);
		listener.Start();
		Log.Info("host", $"host simulado na porta {port}, mandando quadros de teste a {fps} fps");
		var sw = Stopwatch.StartNew();
		while (seconds <= 0 || sw.Elapsed.TotalSeconds < seconds)
		{
			if (!listener.Pending())
			{
				Thread.Sleep(50);
				continue;
			}
			TcpClient c = listener.AcceptTcpClient();
			c.NoDelay = true;
			Log.Info("host", "amigo conectou: " + c.Client.RemoteEndPoint);
			var stats = HostSession(c, fps, seconds > 0 ? seconds - sw.Elapsed.TotalSeconds : 0, log: true);
			Log.Info("host", stats.ToString());
		}
		listener.Stop();
		return 0;
	}

	public sealed class HostResult
	{
		public int FramesSent;

		public int Inputs;

		public uint ButtonsSeen;

		public string Error;

		public override string ToString() =>
			$"{FramesSent} quadros enviados, {Inputs} inputs recebidos, botoes vistos: {Describe(ButtonsSeen)}" + (Error != null ? " | fim: " + Error : "");
	}

	public static HostResult HostSession(TcpClient c, double fps, double seconds, bool log)
	{
		var res = new HostResult();
		NetworkStream ns = c.GetStream();
		bool stop = false;
		var sw = Stopwatch.StartNew();
		var reader = new Thread(() =>
		{
			try
			{
				var br = new BinaryReader(ns);
				uint last = 0;
				while (!stop)
				{
					byte type = br.ReadByte();
					if (type != PacketInput)
					{
						throw new IOException("tipo inesperado do amigo: " + type);
					}
					uint b = br.ReadUInt32();
					float x = br.ReadSingle(), y = br.ReadSingle();
					br.ReadSingle();
					br.ReadSingle();
					lock (res)
					{
						res.Inputs++;
						res.ButtonsSeen |= b;
					}
					if (log && b != last)
					{
						Log.Info("host", $"amigo: {Describe(b)}  eixo ({x:0.00},{y:0.00})");
					}
					last = b;
				}
			}
			catch (Exception ex)
			{
				res.Error ??= ex.Message;
			}
			stop = true;
		}) { IsBackground = true };
		reader.Start();
		var bw = new BinaryWriter(ns);
		try
		{
			while (!stop && (seconds <= 0 || sw.Elapsed.TotalSeconds < seconds))
			{
				byte[] jpg = TestFrame(res.FramesSent, sw.Elapsed.TotalSeconds);
				bw.Write(PacketFrame);
				bw.Write(jpg.Length);
				bw.Write(jpg);
				bw.Flush();
				res.FramesSent++;
				Thread.Sleep((int)(1000 / fps));
			}
		}
		catch (Exception ex)
		{
			res.Error ??= ex.Message;
		}
		stop = true;
		c.Close();
		reader.Join(1000);
		return res;
	}

	// Quadro 640x360 com contador e relogio (para ver o atraso na tela do amigo)
	public static byte[] TestFrame(int n, double t)
	{
		using var bmp = new Bitmap(640, 360);
		using (var g = Graphics.FromImage(bmp))
		{
			g.Clear(Color.FromArgb(40 + n * 3 % 100, 20, 60));
			using var font = new Font("Arial", 28, FontStyle.Bold);
			g.DrawString("NETLAB HOST SIMULADO", font, Brushes.White, 20, 20);
			g.DrawString($"quadro {n}", font, Brushes.Yellow, 20, 120);
			g.DrawString(DateTime.Now.ToString("HH:mm:ss.fff"), font, Brushes.Cyan, 20, 200);
			g.FillRectangle(Brushes.Red, (float)(t * 200 % 600), 300, 40, 40);
		}
		using var ms = new MemoryStream();
		bmp.Save(ms, ImageFormat.Jpeg);
		return ms.ToArray();
	}
}
