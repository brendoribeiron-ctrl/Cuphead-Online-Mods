using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NetLab;

public static class Program
{
	private const string Usage = @"NetLab - testes do online do Cuphead numa maquina so

  proxy     --listen ip:porta --target ip:porta [--delay ms] [--jitter ms] [--loss %] [--dup %]
            [--kbps n] [--rto ms] [--cut-after s] [--freeze-at s --freeze-for s] [--udp|--udp-only] [--seconds s]
            Fica no meio de duas instancias simulando internet ruim. TCP nunca perde bytes:
            'perda' vira atraso de retransmissao (--rto). Com --udp tambem repassa UDP (ai perde de verdade).

  client    --connect ip:porta [--script idle|walk|jump|shoot|join|mash] [--seconds s] [--hz n] [--save-frames pasta]
            Amigo simulado do lobby da main: recebe a tela do host e manda controles roteirizados.

  host      [--port n] [--fps n] [--seconds s]
            Host simulado do lobby da main: manda quadros de teste e mostra os controles do amigo.

  peer      --connect ip:porta | --listen porta  [--seconds s] [--script ...]
            Par simulado do protocolo novo (NetSession + netcode 0x20..0x3F).

  selftest  [--quick]
            Roda todos os testes automaticos (sem abrir o jogo). Sai com codigo 0 se tudo passou.
";

	// Sem isso o Windows arredonda Sleep/Task.Delay para ~15.6 ms e a latencia simulada sai errada
	[System.Runtime.InteropServices.DllImport("winmm.dll")]
	private static extern uint timeBeginPeriod(uint ms);

	public static int Main(string[] argv)
	{
		timeBeginPeriod(1);
		if (argv.Length == 0)
		{
			Console.WriteLine(Usage);
			return 2;
		}
		var a = new Args(argv.Skip(1));
		switch (argv[0])
		{
		case "proxy":
			return Proxy.Run(a);
		case "client":
			return Legacy.RunClient(a);
		case "host":
			return Legacy.RunHost(a);
		case "peer":
			return SessionPeer.Run(a);
		case "selftest":
			return SelfTest.Run(a);
		default:
			Console.WriteLine(Usage);
			return 2;
		}
	}

	public static int FreePort()
	{
		var l = new TcpListener(IPAddress.Loopback, 0);
		l.Start();
		int p = ((IPEndPoint)l.LocalEndpoint).Port;
		l.Stop();
		return p;
	}
}

public static class SelfTest
{
	private static int passed;

	private static readonly List<string> failures = new();

	private static void Check(string name, bool ok, string detail)
	{
		if (ok)
		{
			passed++;
			Log.Info("PASSOU", name + " | " + detail);
		}
		else
		{
			failures.Add(name + ": " + detail);
			Log.Info("FALHOU", name + " | " + detail);
		}
	}

	public static int Run(Args a)
	{
		bool quick = a.Flag("quick");
		ProxyIntegrity("proxy TCP limpo", new LinkConditions());
		ProxyIntegrity("proxy TCP 80ms+-40 perda 5%", new LinkConditions { DelayMs = 80, JitterMs = 40, LossPct = 5 });
		ProxyLatency();
		ProxyBandwidth();
		ProxyCut();
		UdpLoss();
		Framing.SelfTest(Check);
		double secs = quick ? 3 : 8;
		LegacyEndToEnd("lobby main, rede local", new LinkConditions(), secs, minFps: 15);
		LegacyEndToEnd("lobby main, 60ms perda 2%", new LinkConditions { DelayMs = 60, JitterMs = 15, LossPct = 2 }, secs, minFps: 10);
		LegacyEndToEnd("lobby main, 2 Mbps", new LinkConditions { DelayMs = 30, Kbps = 2000 }, secs, minFps: 5);
		SessionPeer.SelfTest(Check, secs);
		Console.WriteLine();
		Console.WriteLine($"{passed} passaram, {failures.Count} falharam");
		foreach (string f in failures)
		{
			Console.WriteLine("  FALHOU: " + f);
		}
		return failures.Count == 0 ? 0 : 1;
	}

	// Liga um proxy entre 127.0.0.1:porta e um destino, retorna o endpoint do proxy
	public static (IPEndPoint ep, CancellationTokenSource cts) StartProxy(int targetPort, LinkConditions c, bool udp = false)
	{
		int port = Program.FreePort();
		var ep = new IPEndPoint(IPAddress.Loopback, port);
		var target = new IPEndPoint(IPAddress.Loopback, targetPort);
		var cts = new CancellationTokenSource();
		_ = udp ? Proxy.RunUdp(ep, target, c, cts.Token) : Proxy.RunTcp(ep, target, c, cts.Token);
		Thread.Sleep(100);
		return (ep, cts);
	}

	// Manda 2 MB aleatorios pelo proxy e confere se chegam iguais e na ordem (eco)
	private static void ProxyIntegrity(string name, LinkConditions c)
	{
		var echo = new TcpListener(IPAddress.Loopback, 0);
		echo.Start();
		int ep = ((IPEndPoint)echo.LocalEndpoint).Port;
		_ = Task.Run(() =>
		{
			using TcpClient s = echo.AcceptTcpClient();
			NetworkStream ns = s.GetStream();
			byte[] b = new byte[8192];
			int r;
			while ((r = ns.Read(b, 0, b.Length)) > 0)
			{
				ns.Write(b, 0, r);
			}
		});
		var (pep, cts) = StartProxy(ep, c);
		byte[] data = RandomNumberGenerator.GetBytes(2 << 20);
		byte[] back = new byte[data.Length];
		try
		{
			using var cl = new TcpClient();
			cl.Connect(pep);
			NetworkStream ns = cl.GetStream();
			var w = Task.Run(() =>
			{
				for (int i = 0; i < data.Length; i += 1400)
				{
					ns.Write(data, i, Math.Min(1400, data.Length - i));
				}
			});
			int got = 0;
			ns.ReadTimeout = 20000;
			while (got < back.Length)
			{
				int r = ns.Read(back, got, back.Length - got);
				if (r <= 0)
				{
					break;
				}
				got += r;
			}
			w.Wait();
			Check(name, got == data.Length && data.AsSpan().SequenceEqual(back), $"{got} de {data.Length} bytes voltaram iguais");
		}
		catch (Exception ex)
		{
			Check(name, false, ex.Message);
		}
		cts.Cancel();
		echo.Stop();
	}

	// Ping pelo proxy com 50ms de ida deve dar ~100ms
	private static void ProxyLatency()
	{
		var echo = new TcpListener(IPAddress.Loopback, 0);
		echo.Start();
		int ep = ((IPEndPoint)echo.LocalEndpoint).Port;
		_ = Task.Run(() =>
		{
			using TcpClient s = echo.AcceptTcpClient();
			s.NoDelay = true;
			NetworkStream ns = s.GetStream();
			int b;
			while ((b = ns.ReadByte()) >= 0)
			{
				ns.WriteByte((byte)b);
			}
		});
		var (pep, cts) = StartProxy(ep, new LinkConditions { DelayMs = 50 });
		using var cl = new TcpClient { NoDelay = true };
		cl.Connect(pep);
		NetworkStream cs = cl.GetStream();
		var times = new List<double>();
		for (int i = 0; i < 10; i++)
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			cs.WriteByte(1);
			cs.ReadByte();
			times.Add(sw.Elapsed.TotalMilliseconds);
		}
		double avg = times.Average();
		Check("proxy latencia 50ms de ida", avg > 95 && avg < 140, $"ping medio {avg:0} ms (esperado ~100)");
		cts.Cancel();
		echo.Stop();
	}

	// 1 MB a 4000 kbps deve levar ~2s
	private static void ProxyBandwidth()
	{
		var sink = new TcpListener(IPAddress.Loopback, 0);
		sink.Start();
		int ep = ((IPEndPoint)sink.LocalEndpoint).Port;
		var doneAt = new TaskCompletionSource<double>();
		var sw = System.Diagnostics.Stopwatch.StartNew();
		_ = Task.Run(() =>
		{
			using TcpClient s = sink.AcceptTcpClient();
			NetworkStream ns = s.GetStream();
			byte[] b = new byte[65536];
			long total = 0;
			int r;
			while (total < 1 << 20 && (r = ns.Read(b, 0, b.Length)) > 0)
			{
				total += r;
			}
			doneAt.SetResult(sw.Elapsed.TotalSeconds);
		});
		var (pep, cts) = StartProxy(ep, new LinkConditions { Kbps = 4000 });
		using var cl = new TcpClient();
		cl.Connect(pep);
		sw.Restart();
		cl.GetStream().Write(new byte[1 << 20]);
		bool finished = doneAt.Task.Wait(10000);
		double t = finished ? doneAt.Task.Result : -1;
		Check("proxy limite de banda 4 Mbps", t > 1.7 && t < 3.0, $"1 MB levou {t:0.00}s (esperado ~2.1)");
		cts.Cancel();
		sink.Stop();
	}

	// cut-after derruba os dois lados
	private static void ProxyCut()
	{
		var srv = new TcpListener(IPAddress.Loopback, 0);
		srv.Start();
		int ep = ((IPEndPoint)srv.LocalEndpoint).Port;
		_ = Task.Run(() =>
		{
			TcpClient s = srv.AcceptTcpClient();
			Thread.Sleep(5000);
			s.Close();
		});
		var (pep, cts) = StartProxy(ep, new LinkConditions { CutAfterS = 1 });
		using var cl = new TcpClient();
		cl.Connect(pep);
		var sw = System.Diagnostics.Stopwatch.StartNew();
		bool closed;
		try
		{
			cl.GetStream().ReadTimeout = 4000;
			closed = cl.GetStream().Read(new byte[10]) <= 0;
		}
		catch (IOException)
		{
			closed = true;
		}
		double t = sw.Elapsed.TotalSeconds;
		Check("proxy corta conexao", closed && t < 3, $"conexao caiu depois de {t:0.0}s");
		cts.Cancel();
		srv.Stop();
	}

	// UDP com 20% de perda: chegam ~80%
	private static void UdpLoss()
	{
		using var srv = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
		int sp = ((IPEndPoint)srv.Client.LocalEndPoint).Port;
		var (pep, cts) = StartProxy(sp, new LinkConditions { LossPct = 20, DelayMs = 10, JitterMs = 10 }, udp: true);
		using var cl = new UdpClient();
		cl.Connect(pep);
		for (int i = 0; i < 1000; i++)
		{
			cl.Send(BitConverter.GetBytes(i), 4);
			if (i % 50 == 0)
			{
				Thread.Sleep(1);
			}
		}
		int got = 0;
		srv.Client.ReceiveTimeout = 500;
		try
		{
			while (true)
			{
				IPEndPoint any = null;
				srv.Receive(ref any);
				got++;
			}
		}
		catch (SocketException)
		{
		}
		Check("proxy UDP perda 20%", got > 720 && got < 880, $"{got} de 1000 pacotes chegaram");
		cts.Cancel();
	}

	private static void LegacyEndToEnd(string name, LinkConditions c, double seconds, double minFps)
	{
		var srv = new TcpListener(IPAddress.Loopback, 0);
		srv.Start();
		int port = ((IPEndPoint)srv.LocalEndpoint).Port;
		Legacy.HostResult hr = null;
		var hostTask = Task.Run(() =>
		{
			TcpClient s = srv.AcceptTcpClient();
			s.NoDelay = true;
			hr = Legacy.HostSession(s, 20, seconds + 1, log: false);
		});
		var (pep, cts) = StartProxy(port, c);
		Legacy.ClientResult cr = Legacy.ClientSession(pep, "mash", seconds, 60);
		hostTask.Wait(5000);
		double fps = cr.Frames / Math.Max(0.1, cr.Seconds);
		uint expected = (1u << 2) | (1u << 3) | (1u << 7);
		bool inputsOk = hr != null && hr.Inputs > seconds * 30 && (hr.ButtonsSeen & expected) == expected;
		Check(name, cr.Ok && fps >= minFps && cr.BadFrames == 0 && inputsOk,
			cr + " | host: " + (hr?.ToString() ?? "nao terminou"));
		cts.Cancel();
		srv.Stop();
	}
}
