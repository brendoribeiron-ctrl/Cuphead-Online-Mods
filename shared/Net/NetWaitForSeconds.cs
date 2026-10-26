using UnityEngine;

// MOD ONLINE: troca o WaitForSeconds do Unity em todo o jogo. O do Unity compara com Time.time, que e
// diferente em cada PC (cada jogo foi aberto numa hora); com arredondamento de float a mesma espera
// terminava um frame antes num PC do que no outro e os jogos se separavam. Este soma Time.deltaTime
// a partir do zero, que e igual nos dois (1/60 por frame no lockstep).
public class NetWaitForSeconds : CustomYieldInstruction
{
	private readonly float seconds;

	private float elapsed;

	private bool waiting;

	public NetWaitForSeconds(float seconds)
	{
		this.seconds = seconds;
	}

	public override bool keepWaiting
	{
		get
		{
			// A mesma instancia pode ser usada em varios yields seguidos
			if (!waiting)
			{
				waiting = true;
				elapsed = 0f;
			}
			elapsed += Time.deltaTime;
			if (elapsed >= seconds)
			{
				waiting = false;
				return false;
			}
			return true;
		}
	}
}
