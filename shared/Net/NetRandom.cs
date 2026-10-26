using System.Diagnostics;
using UnityEngine;

// MOD ONLINE: todo sorteio do jogo passa por aqui (mesmo UnityEngine.Random de sempre). Com
// -netcode-trace cada sorteio vai para o log com quem pediu, para achar o que separa os dois PCs.
public static class NetRandom
{
	public static bool Trace;

	// Sorteios de efeitos (tremida da camera) num gerador separado: a ordem em que rodam em relacao
	// ao resto pode mudar entre os PCs e nao pode gastar o Random da partida. A semente vem do host
	// a cada troca de cena (NetcodeSync.SceneBarrier), entao os valores tambem batem.
	private static System.Random effects = new System.Random(1);

	public static void SeedEffects(int seed)
	{
		effects = new System.Random(seed);
	}

	public static float EffectRange(float min, float max)
	{
		return min + (float)effects.NextDouble() * (max - min);
	}

	public static float value
	{
		get
		{
			float v = UnityEngine.Random.value;
			Log("value", v);
			return v;
		}
	}

	public static Vector2 insideUnitCircle
	{
		get
		{
			Vector2 v = UnityEngine.Random.insideUnitCircle;
			Log("circle", v.x);
			return v;
		}
	}

	public static Vector3 insideUnitSphere
	{
		get
		{
			Vector3 v = UnityEngine.Random.insideUnitSphere;
			Log("sphere", v.x);
			return v;
		}
	}

	public static float Range(float min, float max)
	{
		float v = UnityEngine.Random.Range(min, max);
		Log("range", v);
		return v;
	}

	public static int Range(int min, int max)
	{
		int v = UnityEngine.Random.Range(min, max);
		Log("int", v);
		return v;
	}

	private static void Log(string kind, float v)
	{
		if (!NetcodeSync.InLockstep)
		{
			return;
		}
		NetcodeSync.TraceLine("[Rnd] " + NetcodeSync.Frame + " " + Caller(3) + " < " + Caller(2) + " " + kind + " " + v.ToString("0.###"));
	}

	private static string Caller(int depth)
	{
		System.Reflection.MethodBase method = new StackFrame(depth).GetMethod();
		if (method == null)
		{
			return "?";
		}
		return ((method.DeclaringType != null) ? method.DeclaringType.FullName : "?") + "." + method.Name;
	}
}
