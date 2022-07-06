using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.ParticleSystem;

namespace gooby.NetworkParticleSystem
{
    [RequireComponent(typeof(ParticleSystem))]
    public class NetworkParticleSystem : NetworkBehaviour
    {

        [System.Serializable]
        private struct ParticleSyncData
        {
            public uint Seed;
            public float Time;
            public uint Tick;

            public override string ToString() => $"Seed: {Seed}, PS Time: {Time}, Server tick: {Tick}";
        }

        [SerializeField] private bool _syncSeed = true;

        [SerializeField] private ParticleSystem _particleSystem = null;
        public ParticleSystem ParticleSystem => _particleSystem;

        [Header("Emission Sync")]
        [SerializeField] private bool _syncAllParticles = false;
        private int _particleCount;

        private Particle[] _particles;
        public Particle[] Particles => _particles;

        private List<Vector4> _customData = new List<Vector4>();
        public List<Vector4> CustomData => _customData;

        [SyncVar(OnChange = nameof(OnSeedChanged))]
        private ParticleSyncData _psSyncData;
        private void OnSeedChanged(ParticleSyncData prev, ParticleSyncData cur, bool asServer)
        {
            if (asServer || IsHost) return;

            _particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _particleSystem.randomSeed = cur.Seed;
            _particleSystem.Simulate(cur.Time - OneWayRTT(cur.Tick));
            _particleSystem.Play(true);
        }

        [Header("Events")]
        /// <summary>
        /// Invokes the GameObject that collided with the particle.
        /// </summary>
        public UnityEvent<GameObject> OnParticleCollisionWithGameObject;
        /// <summary>
        /// Invokes the index in the particles array of the particle that was born.
        /// </summary>
        public UnityEvent<int> OnParticleBirth;
        /// <summary>
        /// Invokes the index in the particles array of the particle that died.
        /// </summary>
        public UnityEvent<int> OnParticleDeath;


        private void OnParticleCollision(GameObject other)
        {
            InvokeParticleCollision(other);
        }

        void InvokeParticleCollision(GameObject other)
        {
            OnParticleCollisionWithGameObject?.Invoke(other);
        }

        public override void OnSpawnServer(NetworkConnection connection)
        {
            base.OnSpawnServer(connection);

            if (_syncSeed)
            {
                _psSyncData = new ParticleSyncData()
                {
                    Seed = _particleSystem.randomSeed,
                    Time = _particleSystem.time,
                    Tick = TimeManager.Tick,
                };
            }
        }

        float OneWayRTT(uint serverTick) => (float)TimeManager.TicksToTime(TimeManager.Tick - serverTick);

        private void Awake()
        {
            _particles = new Particle[_particleSystem.main.maxParticles];
        }

        private void LateUpdate()
        {
            if (IsServer && _syncAllParticles)
            {
                GetParticleData();

                if (_particleCount > 0)
                {
                    //Debug.Log($"Particle count: {_particleCount}");

                    for (int i = 0; i < _particleCount; i++)
                    {
                        if (_customData[i].x == 0)
                        {
                            int id = GetUniqueID();
                            // particle born
                            _customData[i] = new Vector4(id, 0, 0, 0);
                            //Debug.Log($"Particle index {i} NEW ID: {_customData[i].x}");

#pragma warning disable 0618
                            RpcEmit(_particles[i].position, _particles[i].velocity,
                                _particles[i].size, _particles[i].remainingLifetime, _particles[i].color,
                                id);
#pragma warning restore 0618

                            OnParticleBirth?.Invoke(i);
                        }
                        else if (_particles[i].remainingLifetime <= 0f)
                        {
                            OnParticleDeath?.Invoke(i);

                            //Debug.Log($"Particle DEAD, index {i} ID: {_customData[i].x}");
                            RpcKill((int)_customData[i].x);
                            _customData[i] = Vector4.zero;
                        }
                    }

                    SetParticleData();
                }
            }
        }

        [ObserversRpc]
        void RpcKill(int id)
        {
            if (IsServer) return;

            //Debug.Log("Killing particle ID: " + id);
            GetParticleData();

            for (int i = 0; i < _particleCount; i++)
            {
                if (_customData[i].x == id)
                {
                    OnParticleDeath?.Invoke(i);

                    //Debug.Log($"Found particle {i} with id {id}");
                    _customData[i] = Vector4.zero;

                    _particles[i].remainingLifetime = 0;
                }
            }

            SetParticleData();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // host check
            if (IsServer) return;

            // disable collision on clients.
            // the server will handle it and send RpcKill's for client to remove them
            var col = _particleSystem.collision;
            col.enabled = false;
        }

        [ObserversRpc]
        void RpcEmit(Vector3 pos, Vector3 vel, float size, float lifetime, Color32 color,
            int id)
        {
            if (IsServer) return;

            // using obsolete on purpose bc it actually works
#pragma warning disable 0618
            _particleSystem.Emit(pos, vel, size, lifetime, color);
#pragma warning restore 0618

            GetParticleData();

            for (int i = 0; i < _particleCount; i++)
            {
                //Debug.Log($"Particle {i} empty id");
                if (_customData[i].x == 0)
                {
                    _customData[i] = new Vector4(id, 0, 0, 0);
                    //Debug.Log($"Particle index {i} NEW ID: {_customData[i].x}");
                    OnParticleBirth?.Invoke(i);
                }
            }

            SetParticleData();
        }

        /// <summary>
        /// Gets an int conversion of a GUID. Used for assigning ID's to particles.
        /// </summary>
        /// <returns></returns>
        int GetUniqueID()
        {
            return Animator.StringToHash(System.Guid.NewGuid().ToString());
        }

        /// <summary>
        /// Gets the current state of the particle system particles.
        /// Returns its current particle count.
        /// </summary>
        /// <returns></returns>
        public int GetParticleData()
        {
            _particleCount = _particleSystem.GetParticles(_particles);
            _particleSystem.GetCustomParticleData(_customData, ParticleSystemCustomData.Custom2);

            return _particleCount;
        }

        /// <summary>
        /// Sets the _customData and _particles to the particle system.
        /// </summary>
        void SetParticleData()
        {
            _particleSystem.SetCustomParticleData(_customData, ParticleSystemCustomData.Custom2);
            _particleSystem.SetParticles(_particles, _particleCount);
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            if (_particleSystem == null)
                _particleSystem = GetComponent<ParticleSystem>();
        }

    }

}
