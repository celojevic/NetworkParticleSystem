using System.Collections;
using FishNet.Object;
using UnityEngine;

namespace gooby.NetworkParticleSystem.Examples.FishNetSurvivors
{
    public class EnemyParticles : NetworkBehaviour
    {

        [SerializeField] private ExpGemParticles _expGemParticles = null;
        [SerializeField] private float _moveSpeed = 1f;

        private NetworkParticleSystem _nps;
        private WaitForSeconds _wfs = new WaitForSeconds(1f);
        private PlayerController _player;

        private void Awake()
        {
            _nps = GetComponent<NetworkParticleSystem>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            StartCoroutine(AwaitPlayerSpawn());
        }

        IEnumerator AwaitPlayerSpawn()
        {
            while ((_player = FindObjectOfType<PlayerController>())==null)
                yield return _wfs;

            PlayerSpawned();
        }

        void PlayerSpawned()
        {
            _nps.OnParticleBirth.AddListener(OnParticleBorn);
            _nps.OnParticleDeath.AddListener(OnParticleDeath);
            _nps.OnParticleUpdate.AddListener(OnPreParticleUpdate);

            StartCoroutine(SpawnEnemies());
        }

        private void OnPreParticleUpdate()
        {
            // TODO this loops thru all 100 particles, not the living ones
            for (int i = 0; i < _nps.Particles.Length; i++)
            {
                var particle = _nps.Particles[i];
                particle.velocity = (_player.transform.position - particle.position).normalized * _moveSpeed;
                _nps.Particles[i] = particle;
            }
        }

        private void OnParticleDeath(int index)
        {
            _expGemParticles.SpawnGem(_nps.Particles[index].position);
        }

        private void OnParticleBorn(int index)
        {
        }

        IEnumerator SpawnEnemies()
        {
            while (true)
            {
                yield return _wfs;

                _nps.ParticleSystem.Emit(1);
            }
        }



    }
}