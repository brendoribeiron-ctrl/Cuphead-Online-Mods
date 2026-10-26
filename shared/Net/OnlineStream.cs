using System;
using System.Collections;
using Rewired;
using UnityEngine;

// MOD ONLINE: modo tela compartilhada (NetSession.GameMode.Stream). O host roda o jogo,
// manda a imagem da tela para o amigo e recebe os controles dele no player 2.
// O amigo ve a tela do host e manda os proprios controles.
public class OnlineStream : MonoBehaviour
{
	public const byte MsgFrame = 0x40;

	public const byte MsgInput = 0x41;

	private const int StreamWidth = 640;

	private const int StreamHeight = 360;

	private const float StreamFps = 20f;

	private const int JpegQuality = 45;

	private NetSession session;

	private readonly object sync = new object();

	private RenderTexture captureRT;

	private Texture2D captureTex;

	private Texture2D screenTex;

	private float nextCaptureTime;

	private bool capturing;

	private byte[] receivedFrame;

	private Texture2D remoteTex;

	private float lastFrameTime;

	private int framesShown;

	private float fpsTimer;

	private int fpsShown;

	private readonly float[] localAxes = new float[4];

	private bool muted;

	private float savedVolume = 1f;

	private void Start()
	{
		session = NetSession.Instance;
		session.RegisterHandler(MsgFrame, delegate(int from, byte[] payload)
		{
			lock (sync)
			{
				receivedFrame = payload;
			}
		}, onNetworkThread: true);
		session.RegisterHandler(MsgInput, delegate(int from, byte[] payload)
		{
			if (payload.Length >= 20)
			{
				uint buttons = BitConverter.ToUInt32(payload, 0);
				float[] axes = new float[4];
				for (int i = 0; i < 4; i++)
				{
					axes[i] = BitConverter.ToSingle(payload, 4 + i * 4);
				}
				NetInput.ReceiveRemote(buttons, axes);
			}
		}, onNetworkThread: true);
	}

	// Modo tela ativo: partida iniciada no modo Stream (inclui o tempo reconectando)
	public bool Active
	{
		get
		{
			if (session == null || session.Mode != NetSession.GameMode.Stream)
			{
				return false;
			}
			return session.State == NetSession.SessionState.InGame || (session.State == NetSession.SessionState.Reconnecting && remoteTex != null);
		}
	}

	private void Update()
	{
		if (session == null)
		{
			return;
		}
		bool active = Active;
		if (session.IsHost)
		{
			bool peer = active && session.IsConnected(1);
			NetInput.SetRemoteActiveIfChanged(peer);
			// Com o lobby aberto a imagem para (a janela mostra os IPs e codigos do host)
			bool lobbyOpen = OnlineLobby.Instance != null && OnlineLobby.Instance.WindowVisible;
			if (peer && !lobbyOpen && !capturing && Time.unscaledTime >= nextCaptureTime)
			{
				nextCaptureTime = Time.unscaledTime + 1f / StreamFps;
				StartCoroutine(CaptureFrame());
			}
		}
		else
		{
			NetInput.SetRemoteActiveIfChanged(active: false);
		}
		if (session.IsGuest && active)
		{
			ShowRemoteFrames();
			if (session.State == NetSession.SessionState.InGame && session.PeerConnected && ReInput.isReady && ReInput.players.GetPlayer(0) != null)
			{
				NetInput.ReadLocal(ReInput.players.GetPlayer(0), out var buttons, localAxes);
				byte[] payload = new byte[20];
				Array.Copy(BitConverter.GetBytes(buttons), 0, payload, 0, 4);
				for (int i = 0; i < 4; i++)
				{
					Array.Copy(BitConverter.GetBytes(localAxes[i]), 0, payload, 4 + i * 4, 4);
				}
				session.SendLatest(MsgInput, payload);
			}
		}
		else if (!session.InSession && remoteTex != null)
		{
			UnityEngine.Object.Destroy(remoteTex);
			remoteTex = null;
		}
		// O amigo so escuta o proprio jogo parado; tira o som enquanto ve a tela do host
		bool shouldMute = session.IsGuest && active;
		if (shouldMute != muted)
		{
			muted = shouldMute;
			if (muted)
			{
				savedVolume = AudioListener.volume;
				AudioListener.volume = 0f;
			}
			else
			{
				AudioListener.volume = savedVolume;
			}
		}
	}

	private void ShowRemoteFrames()
	{
		byte[] frame;
		lock (sync)
		{
			frame = receivedFrame;
			receivedFrame = null;
		}
		if (frame != null)
		{
			if (remoteTex == null)
			{
				remoteTex = new Texture2D(2, 2, TextureFormat.RGB24, mipmap: false);
			}
			remoteTex.LoadImage(frame);
			lastFrameTime = Time.unscaledTime;
			framesShown++;
		}
		fpsTimer += Time.unscaledDeltaTime;
		if (fpsTimer >= 1f)
		{
			fpsShown = framesShown;
			framesShown = 0;
			fpsTimer = 0f;
		}
	}

	// Le a tela inteira no fim do frame, reduz na GPU para 640x360 e comprime em JPEG
	private IEnumerator CaptureFrame()
	{
		capturing = true;
		yield return new WaitForEndOfFrame();
		capturing = false;
		if (!session.IsConnected(1))
		{
			yield break;
		}
		if (captureRT == null)
		{
			captureRT = new RenderTexture(StreamWidth, StreamHeight, 0, RenderTextureFormat.ARGB32);
			captureRT.Create();
			captureTex = new Texture2D(StreamWidth, StreamHeight, TextureFormat.RGB24, mipmap: false);
		}
		if (screenTex == null || screenTex.width != Screen.width || screenTex.height != Screen.height)
		{
			screenTex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, mipmap: false);
		}
		screenTex.ReadPixels(new Rect(0f, 0f, Screen.width, Screen.height), 0, 0, recalculateMipMaps: false);
		screenTex.Apply(updateMipmaps: false);
		RenderTexture active = RenderTexture.active;
		Graphics.Blit(screenTex, captureRT);
		RenderTexture.active = captureRT;
		captureTex.ReadPixels(new Rect(0f, 0f, StreamWidth, StreamHeight), 0, 0, recalculateMipMaps: false);
		RenderTexture.active = active;
		session.SendLatest(MsgFrame, captureTex.EncodeToJPG(JpegQuality), 1);
	}

	private void OnGUI()
	{
		if (session == null || !session.IsGuest || !Active || remoteTex == null)
		{
			return;
		}
		GUI.depth = -1000;
		GUI.color = Color.black;
		GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
		GUI.color = Color.white;
		GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), remoteTex, ScaleMode.ScaleToFit, alphaBlend: false);
		string text = "ONLINE: vendo a tela do host  |  " + fpsShown + " fps  |  " + session.PingMs + " ms" + ((Time.unscaledTime - lastFrameTime > 2f) ? "  |  SEM IMAGEM" : string.Empty) + "  |  F1 = lobby";
		GUI.Label(new Rect(10f, Screen.height - 30, Screen.width, 30f), text);
	}
}
