using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetLab;

// Condicoes de rede simuladas, iguais nas duas direcoes
public sealed class LinkConditions
{
	public double DelayMs;      // latencia de ida (o ping fica ~2x)

	public double JitterMs;     // variacao aleatoria somada a latencia (0..jitter)

	public double LossPct;      // UDP: pacote some. TCP: trecho atrasa como numa retransmissao

	public double DupPct;       // UDP: pacote chega duplicado

	public double Kbps;         // limite de banda por direcao (0 = sem limite)

	public double RetransmitMs = 250;  // atraso extra de um trecho "perdido" no TCP

	public double CutAfterS;    // derruba a conexao depois de N segundos (testa reconexao)

	public double FreezeAtS;    // congela o trafego a partir de N segundos...

	public double FreezeForS;   // ...por N segundos (testa timeout/heartbeat)

	public static LinkConditions From(Args a) => new()
	{
		DelayMs = a.Num("delay", 0),
		JitterMs = a.Num("jitter", 0),
		LossPct = a.Num("loss", 0),
		DupPct = a.Num("dup", 0),
		Kbps = a.Num("kbps", 0),
		RetransmitMs = a.Num("rto", 250),
		CutAfterS = a.Num("cut-after", 0),
		FreezeAtS = a.Num("freeze-at", 0),
		FreezeForS = a.Num("freeze-for", 0)
	};

	public override string ToString() =>
		$"atraso {DelayMs}ms +-{JitterMs}ms, perda {LossPct}%, dup {DupPct}%, banda {(Kbps > 0 ? Kbps + " kbps" : "livre")}" +
		(CutAfterS > 0 ? $", corta em {CutAfterS}s" : "") + (FreezeForS > 0 ? $", congela {FreezeForS}s em {FreezeAtS}s" : "");
}

// Agenda a hora de entrega de cada trecho/pacote de uma direcao
public sealed class LinkShaper
{
	private readonly LinkConditions c;

	private readonly Random rng;

	private readonly Stopwatch clock;

	private double linkFreeMs;

	private double lastDeliverMs;

	public long Bytes;

	public long Chunks;

	public long Dropped;

	public LinkShaper(LinkConditions c, Stopwatch clock, int seed)
	{
		this.c = c;
		this.clock = clock;
		rng = new Random(seed);
	}

	public double NowMs => clock.Elapsed.TotalMilliseconds;

	public double LossPct => c.LossPct;

	public bool Roll(double pct) => pct > 0 && rng.NextDouble() * 100 < pct;

	// ordered = TCP (nunca reordena); retorna a hora (ms do relogio) para entregar
	public double Schedule(int bytes, bool ordered, bool lost)
	{
		lock (this)
		{
			double now = NowMs;
			Bytes += bytes;
			Chunks++;
			double start = now;
			if (c.FreezeForS > 0)
			{
				double fa = c.FreezeAtS * 1000, fb = fa + c.FreezeForS * 1000;
				if (now >= fa && now < fb)
				{
					start = fb;
				}
			}
			if (c.Kbps > 0)
			{
				start = Math.Max(start, linkFreeMs);
				linkFreeMs = start + bytes * 8.0 / c.Kbps;   // kbps = bits por ms
				start = linkFreeMs;
			}
			double t = start + c.DelayMs + (c.JitterMs > 0 ? rng.NextDouble() * c.JitterMs : 0);
			if (lost)
			{
				t += c.RetransmitMs;
			}
			if (ordered)
			{
				t = Math.Max(t, lastDeliverMs);
				lastDeliverMs = t;
			}
			return t;
		}
	}
}

public static class Proxy
{
	private static readonly Stopwatch Clock = Stopwatch.StartNew();

	public static int Run(Args a)
	{
		IPEndPoint listen = Args.ParseEndPoint(a.Str("listen", "127.0.0.1:7778"), 7778);
		IPEndPoint target = Args.ParseEndPoint(a.Str("target", "127.0.0.1:7777"), 7777);
		LinkConditions cond = LinkConditions.From(a);
		Log.Info("proxy", $"{listen} -> {target} | {cond}");
		var cts = new CancellationTokenSource();
		Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
		var tasks = new List<Task>();
		if (!a.Flag("udp-only"))
		{
			tasks.Add(RunTcp(listen, target, cond, cts.Token));
		}
		if (a.Flag("udp") || a.Flag("udp-only"))
		{
			tasks.Add(RunUdp(listen, target, cond, cts.Token));
		}
		double seconds = a.Num("seconds", 0);
		if (seconds > 0)
		{
			cts.CancelAfter(TimeSpan.FromSeconds(seconds));
		}
		try
		{
			Task.WaitAll(tasks.ToArray());
		}
		catch (AggregateException)
		{
		}
		return 0;
	}

	// ---------- TCP ----------

	public static async Task RunTcp(IPEndPoint listen, IPEndPoint target, LinkConditions cond, CancellationToken ct)
	{
		var listener = new TcpListener(listen);
		listener.Start();
		ct.Register(() => listener.Stop());
		int n = 0;
		while (!ct.IsCancellationRequested)
		{
			TcpClient inbound;
			try
			{
				inbound = await listener.AcceptTcpClientAsync(ct);
			}
			catch (Exception)
			{
				break;
			}
			int id = ++n;
			_ = Task.Run(() => HandleTcp(id, inbound, target, cond, ct));
		}
	}

	private static async Task HandleTcp(int id, TcpClient inbound, IPEndPoint target, LinkConditions cond, CancellationToken ct)
	{
		string tag = "tcp#" + id;
		var outbound = new TcpClient { NoDelay = true };
		inbound.NoDelay = true;
		try
		{
			await outbound.ConnectAsync(target.Address, target.Port, ct);
		}
		catch (Exception ex)
		{
			Log.Info(tag, "nao conectou no destino: " + ex.Message);
			inbound.Close();
			return;
		}
		Log.Info(tag, "conexao de " + inbound.Client.RemoteEndPoint);
		using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var up = new LinkShaper(cond, Clock, id * 2);
		var down = new LinkShaper(cond, Clock, id * 2 + 1);
		Task t1 = PumpTcp(inbound.GetStream(), outbound.GetStream(), up, linkCts.Token);
		Task t2 = PumpTcp(outbound.GetStream(), inbound.GetStream(), down, linkCts.Token);
		Task stats = StatsLoop(tag, up, down, linkCts.Token);
		Task cut = cond.CutAfterS > 0 ? Task.Delay(TimeSpan.FromSeconds(cond.CutAfterS), linkCts.Token) : Task.Delay(Timeout.Infinite, linkCts.Token);
		Task done = await Task.WhenAny(t1, t2, cut);
		if (done == cut && !linkCts.IsCancellationRequested)
		{
			Log.Info(tag, "CORTANDO a conexao (cut-after)");
		}
		linkCts.Cancel();
		// Reset em vez de FIN: simula queda de rede/cabo
		try { inbound.Client.LingerState = new LingerOption(true, 0); } catch { }
		try { outbound.Client.LingerState = new LingerOption(true, 0); } catch { }
		inbound.Close();
		outbound.Close();
		Log.Info(tag, $"fechada. ida {up.Bytes / 1024} KB, volta {down.Bytes / 1024} KB");
	}

	private static async Task PumpTcp(NetworkStream from, NetworkStream to, LinkShaper shaper, CancellationToken ct)
	{
		var queue = new BlockingCollection<(double at, byte[] data)>();
		Task writer = Task.Run(async () =>
		{
			try
			{
				foreach (var (at, data) in queue.GetConsumingEnumerable(ct))
				{
					double wait = at - shaper.NowMs;
					if (wait > 0.5)
					{
						await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
					}
					if (data.Length == 0)
					{
						to.Socket.Shutdown(SocketShutdown.Send);
						return;
					}
					await to.WriteAsync(data, ct);
				}
			}
			catch (Exception)
			{
			}
		});
		byte[] buf = new byte[16384];
		try
		{
			while (!ct.IsCancellationRequested)
			{
				int r = await from.ReadAsync(buf, ct);
				if (r <= 0)
				{
					queue.Add((shaper.Schedule(0, true, false), Array.Empty<byte>()));
					break;
				}
				byte[] chunk = buf.AsSpan(0, r).ToArray();
				queue.Add((shaper.Schedule(r, true, shaper.Roll(shaper.LossPct)), chunk));
			}
		}
		catch (Exception)
		{
		}
		queue.CompleteAdding();
		await writer;
	}

	private static async Task StatsLoop(string tag, LinkShaper up, LinkShaper down, CancellationToken ct)
	{
		long lu = 0, ld = 0;
		while (!ct.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(2000, ct);
			}
			catch (Exception)
			{
				return;
			}
			long u = up.Bytes, d = down.Bytes;
			Log.Info(tag, $"ida {(u - lu) / 2048.0:0.0} KB/s, volta {(d - ld) / 2048.0:0.0} KB/s");
			lu = u;
			ld = d;
		}
	}

	// ---------- UDP ----------

	public static async Task RunUdp(IPEndPoint listen, IPEndPoint target, LinkConditions cond, CancellationToken ct)
	{
		var front = new UdpClient(listen);
		ct.Register(() => front.Close());
		var peers = new ConcurrentDictionary<string, UdpClient>();
		var upShaper = new LinkShaper(cond, Clock, 101);
		var downShaper = new LinkShaper(cond, Clock, 202);
		Log.Info("udp", "ouvindo em " + listen);
		_ = StatsLoop("udp", upShaper, downShaper, ct);
		while (!ct.IsCancellationRequested)
		{
			UdpReceiveResult r;
			try
			{
				r = await front.ReceiveAsync(ct);
			}
			catch (Exception)
			{
				break;
			}
			IPEndPoint src = r.RemoteEndPoint;
			UdpClient back = peers.GetOrAdd(src.ToString(), key =>
			{
				var b = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
				b.Connect(target);
				Log.Info("udp", "novo cliente " + src);
				_ = Task.Run(async () =>
				{
					while (!ct.IsCancellationRequested)
					{
						UdpReceiveResult rr;
						try
						{
							rr = await b.ReceiveAsync(ct);
						}
						catch (Exception)
						{
							return;
						}
						SendShaped(downShaper, cond, rr.Buffer, d => front.SendAsync(d, d.Length, src), ct);
					}
				});
				return b;
			});
			SendShaped(upShaper, cond, r.Buffer, d => back.SendAsync(d, d.Length), ct);
		}
	}

	private static void SendShaped(LinkShaper s, LinkConditions cond, byte[] data, Func<byte[], Task<int>> send, CancellationToken ct)
	{
		if (cond.CutAfterS > 0 && s.NowMs > cond.CutAfterS * 1000)
		{
			s.Dropped++;
			return;
		}
		if (s.Roll(cond.LossPct))
		{
			s.Dropped++;
			return;
		}
		int copies = s.Roll(cond.DupPct) ? 2 : 1;
		for (int i = 0; i < copies; i++)
		{
			double at = s.Schedule(data.Length, ordered: false, lost: false);
			_ = Task.Run(async () =>
			{
				double wait = at - s.NowMs;
				if (wait > 0.5)
				{
					await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
				}
				try
				{
					await send(data);
				}
				catch (Exception)
				{
				}
			});
		}
	}
}
