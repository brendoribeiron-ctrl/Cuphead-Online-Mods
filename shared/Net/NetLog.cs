using System;
using System.IO;
using UnityEngine;

// MOD ONLINE: log do online. Vai para o log do Unity e para um arquivo: o de -onlinelog <arquivo>
// ou, sem ele, %APPDATA%\CupheadOnline\logs\online-<data>.log (um por vez que o jogo abre,
// guardando os 10 mais novos). Erros e excecoes do jogo tambem entram no arquivo.
public static class NetLog
{
	private const int KeepLogs = 10;

	private static readonly object sync = new object();

	private static string path;

	private static bool initialized;

	public static string LogPath
	{
		get
		{
			Init();
			return path;
		}
	}

	public static void Init()
	{
		if (initialized)
		{
			return;
		}
		initialized = true;
		string[] args = Environment.GetCommandLineArgs();
		for (int i = 0; i + 1 < args.Length; i++)
		{
			if (args[i].ToLowerInvariant() == "-onlinelog")
			{
				path = args[i + 1];
			}
		}
		if (path == null)
		{
			try
			{
				string dir = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CupheadOnline"), "logs");
				Directory.CreateDirectory(dir);
				string[] old = Directory.GetFiles(dir, "online-*.log");
				Array.Sort(old);
				for (int j = 0; j < old.Length - (KeepLogs - 1); j++)
				{
					File.Delete(old[j]);
				}
				path = Path.Combine(dir, "online-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
			}
			catch (Exception)
			{
			}
		}
		try
		{
			Application.logMessageReceivedThreaded += OnUnityLog;
		}
		catch (Exception)
		{
		}
		Write("jogo aberto: " + string.Join(" ", args) + " | mod " + NetProtocol.BuildHash.ToString("x8") + " protocolo " + NetProtocol.Version);
	}

	private static void OnUnityLog(string condition, string stackTrace, LogType type)
	{
		if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
		{
			Write("[" + type + "] " + condition + Environment.NewLine + stackTrace);
		}
	}

	public static void Info(string message)
	{
		Init();
		Debug.Log("[Online] " + message);
		Write(message);
	}

	private static void Write(string message)
	{
		if (path == null)
		{
			return;
		}
		lock (sync)
		{
			try
			{
				File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
			}
			catch (Exception)
			{
			}
		}
	}
}
