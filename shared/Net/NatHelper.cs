using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Xml;
using UnityEngine;

// MOD ONLINE: abre a porta do host no roteador (UPnP) e descobre o IP publico
// (UPnP ou STUN), para montar o codigo de sala da internet.
public static class NatHelper
{
	private static readonly string[] StunServers = new string[3] { "stun.l.google.com:19302", "stun1.l.google.com:19302", "stun.cloudflare.com:3478" };

	private static readonly object sync = new object();

	private static Thread thread;

	private static string controlUrl;

	private static string serviceType;

	private static int mappedPort;

	public static volatile bool Busy;

	public static volatile bool PortOpened;

	// Null enquanto nao descobriu
	public static volatile string PublicIp;

	public static volatile string Status = string.Empty;

	public static void Begin(int port)
	{
		Close();
		Busy = true;
		PortOpened = false;
		PublicIp = null;
		Status = "Abrindo a porta no roteador...";
		thread = new Thread((ThreadStart)delegate
		{
			Run(port);
		});
		thread.IsBackground = true;
		thread.Start();
	}

	// Remove o mapeamento da porta (em segundo plano, nao trava o jogo)
	public static void Close()
	{
		string url;
		string service;
		int port;
		lock (sync)
		{
			url = controlUrl;
			service = serviceType;
			port = mappedPort;
			mappedPort = 0;
		}
		PortOpened = false;
		PublicIp = null;
		Status = string.Empty;
		if (url == null || port == 0)
		{
			return;
		}
		Thread t = new Thread((ThreadStart)delegate
		{
			try
			{
				Soap(url, service, "DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol>");
			}
			catch (Exception)
			{
			}
		});
		t.IsBackground = true;
		t.Start();
	}

	private static void Run(int port)
	{
		string upnpIp = null;
		try
		{
			upnpIp = TryUpnp(port);
		}
		catch (Exception ex)
		{
			NetLog.Info("UPnP falhou: " + ex.Message);
		}
		string stunIp = null;
		try
		{
			stunIp = TryStun();
		}
		catch (Exception ex2)
		{
			NetLog.Info("STUN falhou: " + ex2.Message);
		}
		string ip = stunIp ?? upnpIp;
		if (ip != null && IsPrivate(ip))
		{
			ip = null;
		}
		PublicIp = ip;
		if (ip == null)
		{
			Status = "Nao achei o IP publico. Na internet use Radmin VPN/ZeroTier e o codigo de rede local.";
		}
		else if (PortOpened && upnpIp != null && stunIp != null && upnpIp != stunIp)
		{
			Status = "Seu provedor usa CGNAT (IP compartilhado): o codigo da internet nao deve funcionar. Use Radmin VPN/ZeroTier.";
		}
		else if (PortOpened)
		{
			Status = "Porta " + port + " aberta no roteador. O codigo da internet deve funcionar.";
		}
		else
		{
			Status = "O roteador nao abriu a porta sozinho (UPnP desligado?). Abra a porta TCP " + port + " ou use Radmin VPN/ZeroTier.";
		}
		Busy = false;
	}

	// ---------- UPnP ----------

	private static string TryUpnp(int port)
	{
		foreach (string location in DiscoverGateways())
		{
			string url;
			string service;
			if (!FindWanService(location, out url, out service))
			{
				continue;
			}
			Uri uri = new Uri(url);
			string localIp = LocalAddressTowards(uri.Host, uri.Port);
			string args = "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol><NewInternalPort>" + port + "</NewInternalPort><NewInternalClient>" + localIp + "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>Cuphead Online</NewPortMappingDescription><NewLeaseDuration>";
			bool mapped = false;
			try
			{
				Soap(url, service, "AddPortMapping", args + "7200</NewLeaseDuration>");
				mapped = true;
			}
			catch (Exception)
			{
				// Alguns roteadores so aceitam mapeamento permanente
				try
				{
					Soap(url, service, "AddPortMapping", args + "0</NewLeaseDuration>");
					mapped = true;
				}
				catch (Exception ex)
				{
					NetLog.Info("AddPortMapping falhou: " + ex.Message);
				}
			}
			if (mapped)
			{
				lock (sync)
				{
					controlUrl = url;
					serviceType = service;
					mappedPort = port;
				}
				PortOpened = true;
			}
			try
			{
				string response = Soap(url, service, "GetExternalIPAddress", string.Empty);
				string ip = Between(response, "<NewExternalIPAddress>", "</NewExternalIPAddress>");
				if (!string.IsNullOrEmpty(ip))
				{
					return ip.Trim();
				}
			}
			catch (Exception)
			{
			}
			if (mapped)
			{
				return null;
			}
		}
		return null;
	}

	private static List<string> DiscoverGateways()
	{
		List<string> list = new List<string>();
		using (UdpClient udp = new UdpClient(0))
		{
			udp.Client.ReceiveTimeout = 500;
			IPEndPoint ssdp = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
			string[] targets = new string[3] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANPPPConnection:1" };
			foreach (string st in targets)
			{
				byte[] req = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: " + st + "\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n\r\n");
				udp.Send(req, req.Length, ssdp);
			}
			DateTime end = DateTime.UtcNow.AddSeconds(3.0);
			while (DateTime.UtcNow < end)
			{
				byte[] data;
				try
				{
					IPEndPoint from = null;
					data = udp.Receive(ref from);
				}
				catch (SocketException)
				{
					continue;
				}
				foreach (string line in Encoding.ASCII.GetString(data).Split('\n'))
				{
					if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
					{
						string loc = line.Substring(9).Trim();
						if (!list.Contains(loc))
						{
							list.Add(loc);
						}
					}
				}
			}
		}
		return list;
	}

	private static bool FindWanService(string location, out string url, out string service)
	{
		url = null;
		service = null;
		string xml;
		try
		{
			HttpWebRequest req = (HttpWebRequest)WebRequest.Create(location);
			req.Timeout = 3000;
			req.ReadWriteTimeout = 3000;
			using (WebResponse resp = req.GetResponse())
			using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
			{
				xml = reader.ReadToEnd();
			}
		}
		catch (Exception)
		{
			return false;
		}
		XmlDocument doc = new XmlDocument();
		doc.LoadXml(xml);
		Uri baseUri = new Uri(location);
		XmlNodeList bases = doc.GetElementsByTagName("URLBase");
		if (bases.Count > 0 && !string.IsNullOrEmpty(bases[0].InnerText.Trim()))
		{
			baseUri = new Uri(bases[0].InnerText.Trim());
		}
		foreach (XmlNode node in doc.GetElementsByTagName("service"))
		{
			string type = null;
			string control = null;
			foreach (XmlNode child in node.ChildNodes)
			{
				if (child.LocalName == "serviceType")
				{
					type = child.InnerText.Trim();
				}
				else if (child.LocalName == "controlURL")
				{
					control = child.InnerText.Trim();
				}
			}
			if (type != null && control != null && (type.IndexOf("WANIPConnection") >= 0 || type.IndexOf("WANPPPConnection") >= 0))
			{
				url = new Uri(baseUri, control).ToString();
				service = type;
				return true;
			}
		}
		return false;
	}

	private static string Soap(string url, string service, string action, string args)
	{
		string body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:" + action + " xmlns:u=\"" + service + "\">" + args + "</u:" + action + "></s:Body></s:Envelope>";
		byte[] bytes = Encoding.UTF8.GetBytes(body);
		HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
		req.Method = "POST";
		req.Timeout = 4000;
		req.ReadWriteTimeout = 4000;
		req.ContentType = "text/xml; charset=\"utf-8\"";
		req.Headers.Add("SOAPACTION", "\"" + service + "#" + action + "\"");
		req.ServicePoint.Expect100Continue = false;
		req.ContentLength = bytes.Length;
		using (Stream s = req.GetRequestStream())
		{
			s.Write(bytes, 0, bytes.Length);
		}
		using (WebResponse resp = req.GetResponse())
		using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
		{
			return reader.ReadToEnd();
		}
	}

	private static string LocalAddressTowards(string host, int port)
	{
		using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
		{
			s.Connect(host, port);
			return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
		}
	}

	// ---------- STUN ----------

	private static string TryStun()
	{
		foreach (string server in StunServers)
		{
			try
			{
				string[] parts = server.Split(':');
				IPAddress address = null;
				foreach (IPAddress a in Dns.GetHostAddresses(parts[0]))
				{
					if (a.AddressFamily == AddressFamily.InterNetwork)
					{
						address = a;
						break;
					}
				}
				if (address == null)
				{
					continue;
				}
				string ip = StunQuery(new IPEndPoint(address, int.Parse(parts[1])));
				if (ip != null)
				{
					return ip;
				}
			}
			catch (Exception)
			{
			}
		}
		return null;
	}

	private static string StunQuery(IPEndPoint server)
	{
		byte[] req = new byte[20];
		req[1] = 1;
		req[4] = 0x21;
		req[5] = 0x12;
		req[6] = 0xA4;
		req[7] = 0x42;
		byte[] tx = new byte[12];
		new System.Random().NextBytes(tx);
		Array.Copy(tx, 0, req, 8, 12);
		using (UdpClient udp = new UdpClient(0))
		{
			udp.Client.ReceiveTimeout = 1500;
			for (int attempt = 0; attempt < 2; attempt++)
			{
				udp.Send(req, req.Length, server);
				byte[] resp;
				try
				{
					IPEndPoint from = null;
					resp = udp.Receive(ref from);
				}
				catch (SocketException)
				{
					continue;
				}
				if (resp.Length < 20 || resp[0] != 1 || resp[1] != 1)
				{
					continue;
				}
				int pos = 20;
				while (pos + 4 <= resp.Length)
				{
					int type = (resp[pos] << 8) | resp[pos + 1];
					int len = (resp[pos + 2] << 8) | resp[pos + 3];
					int value = pos + 4;
					if (value + len > resp.Length)
					{
						break;
					}
					if ((type == 0x0020 || type == 0x0001) && len >= 8 && resp[value + 1] == 1)
					{
						byte[] ip = new byte[4];
						for (int i = 0; i < 4; i++)
						{
							ip[i] = resp[value + 4 + i];
							if (type == 0x0020)
							{
								ip[i] ^= req[4 + i];
							}
						}
						return new IPAddress(ip).ToString();
					}
					pos = value + ((len + 3) & ~3);
				}
			}
		}
		return null;
	}


	// ---------- util ----------

	public static bool IsPrivate(string ip)
	{
		IPAddress address;
		try
		{
			address = IPAddress.Parse(ip);
		}
		catch (Exception)
		{
			return true;
		}
		byte[] b = address.GetAddressBytes();
		if (b.Length != 4)
		{
			return true;
		}
		return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] < 128);
	}

	private static string Between(string text, string start, string end)
	{
		int a = text.IndexOf(start);
		if (a < 0)
		{
			return null;
		}
		a += start.Length;
		int b = text.IndexOf(end, a);
		return (b < 0) ? null : text.Substring(a, b - a);
	}
}
