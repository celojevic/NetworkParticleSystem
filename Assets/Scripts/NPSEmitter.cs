using FishNet.Object;
using System.Collections;
using UnityEngine;

namespace gooby.NetworkParticleSystem
{

    /// <summary>
    /// Example emitter object that only emits particles on the server. 
    /// The NetworkParticleSystem will handle the birth/death of the particles.
    /// </summary>
    public class NPSEmitter : NetworkBehaviour
    {

        [Tooltip("The amount of particles to emit per burst.")]
        public int EmitCount = 1;
        [Tooltip("Delay between bursts, in seconds.")]
        public float Delay = 1f;

        /// <summary>
        /// Particle system component. 
        /// No need for NetworkParticleSystem as long as you emit on the server. 
        /// </summary>
        private ParticleSystem _ps;

        private void Awake()
        {
            _ps = GetComponent<ParticleSystem>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            StartCoroutine(Emit());
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            StopAllCoroutines();
        }

        /// <summary>
        /// Emits EmitCount particle(s) every Delay seconds.
        /// </summary>
        /// <returns></returns>
        IEnumerator Emit()
        {
            while (true)
            {
                _ps.Emit(EmitCount);

                yield return new WaitForSeconds(Delay);
            }
        }

    }

}
