using FishNet.Connection;
using FishNet.Managing.Logging;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.ParticleSystem;

namespace gooby.NetworkParticleSystem
{

    /// <summary>
    /// Syncs a Unity ParticleSystem over the network.
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public class NetworkParticleSystem : NetworkBehaviour
    {

        [System.Serializable]
        private struct NetParticle
        {
            public int Id;
            public Vector3 Position;
            public Vector3 Velocity;
        }

        [System.Serializable]
        private struct ParticleSyncData
        {
            public uint Seed;
            public float Time;
            public uint Tick;

            public override string ToString() => $"Seed: {Seed}, PS Time: {Time}, Server tick: {Tick}";
        }

        private System.Random _prng = new System.Random();

        [Tooltip("True to sync particle system autoplay data over the network " +
            "so it appears the same to clients as it does on the server.")]
        [SerializeField] 
        private bool _syncSeed = true;

        [Tooltip("True to sync the full particle state to clients when they join.")]
        [SerializeField] 
        private bool _syncFullStateOnJoin = false;

        [Tooltip("The sibling particle system component to sync over the network.")]
        [SerializeField] private ParticleSystem _particleSystem = null;
        public ParticleSystem ParticleSystem => _particleSystem;

        [Header("Emission Sync")]
        [Tooltip("True to sync the birth and death of particles over the network. " +
            "You probably want this on unless you are handling collisions on the client.")]
        [SerializeField] private bool _syncAllParticles = false;
        private int _particleCount;

        /// <summary>
        /// Particle cache.
        /// </summary>
        private Particle[] _particles;
        public Particle[] Particles => _particles;
        /// <summary>
        /// Extra particle cache in case particles are modified, we can detect changes.
        /// </summary>
        private Particle[] _particleCache;

        [SyncObject]
        private readonly SyncDictionary<int, NetParticle> _changedParticles = new SyncDictionary<int, NetParticle>();

        private readonly Dictionary<int, Particle> _livingParticles = new Dictionary<int, Particle>();
        public Dictionary<int, Particle> LivingParticles => _livingParticles;

        /// <summary>
        /// Particle system custom data cache.
        /// </summary>
        private List<Vector4> _customData = new List<Vector4>();
        public List<Vector4> CustomData => _customData;

        [SyncVar(OnChange = nameof(OnSyncDataChanged))]
        private ParticleSyncData _psSyncData;
        private void OnSyncDataChanged(ParticleSyncData prev, ParticleSyncData cur, bool asServer)
        {
            if (asServer || IsHost) return;

            _particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _particleSystem.randomSeed = cur.Seed;
            _particleSystem.Simulate(cur.Time - OneWayRTT(cur.Tick));
            _particleSystem.Play(true);
        }

        [Header("Events")]
        [Tooltip("Invokes the GameObject that collided with the particle.")]
        public UnityEvent<GameObject> OnParticleCollisionWithGameObject;
        [Tooltip("Invokes the index in the particles array of the particle that was born.")]
        public UnityEvent<int> OnParticleBirth;
        [Tooltip("Invokes the index in the particles array of the particle that died.")]
        public UnityEvent<int> OnParticleDeath;
        [Tooltip("Invoked before particles are updated (in LateUpdate). " +
            "Use this event to modify position, velocity, etc. of a particle.")]
        public UnityEvent OnParticleUpdate;

        [Header("Debug")]
        [Tooltip("Debug log level.")]
        [SerializeField] private LoggingType _logLevel = LoggingType.Error;

        private void Awake()
        {
            _particles = new Particle[_particleSystem.main.maxParticles];
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            TimeManager.OnLateUpdate += TimeManager_OnLateUpdate;
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();

            if (TimeManager!=null)
                TimeManager.OnLateUpdate += TimeManager_OnLateUpdate;
        }

        private void TimeManager_OnLateUpdate()
        {
            if (IsServer && _syncAllParticles)
            {
                GetParticleData();

                if (_particleCount > 0)
                {
                    OnParticleUpdate?.Invoke();

                    for (int i = 0; i < _particleCount; i++)
                    {
                        // particle born
                        if (_customData[i].x == 0)
                        {
                            int id = GetUniqueID();
                            _customData[i] = new Vector4(id, 0, 0, 0);

                            if (_logLevel >= LoggingType.Common)
                                Debug.Log($"Particle BORN, index {i}, NEW ID: {_customData[i].x}");

#pragma warning disable 0618
                            RpcEmit(_particles[i].position, _particles[i].velocity,
                                _particles[i].size, _particles[i].remainingLifetime, _particles[i].color,
                                id);
#pragma warning restore 0618

                            _livingParticles.Add(id, _particles[i]);
                            OnParticleBirth?.Invoke(i);
                        }
                        // particle died
                        else if (_particles[i].remainingLifetime <= 0f)
                        {
                            OnParticleDeath?.Invoke(i);

                            if (_logLevel >= LoggingType.Common)
                                Debug.Log($"Particle DEAD, index {i} ID: {_customData[i].x}");

                            int particleId = (int)_customData[i].x;
                            _livingParticles.Remove(particleId);
                            RpcKill(particleId);
                            _customData[i] = Vector4.zero;
                        }

                        //DetectParticleChanges(i);
                    }

                    SetParticleData();
                    _changedParticles.Clear();
                }
            }
        }

        private void DetectParticleChanges(int i)
        {
            NetParticle netParticle = new NetParticle();

            if (_particles[i].position != _particleCache[i].position)
            {
                netParticle.Position = _particles[i].position;
                Debug.Log("Particle pos changed ");
            }

            if (_particles[i].velocity != _particleCache[i].velocity)
            {
                netParticle.Velocity = _particles[i].velocity;
                Debug.Log("Particle vel changed "+netParticle.Velocity);
            }

            if (!netParticle.Equals(default))
            {
                netParticle.Id = (int)_customData[i].x;
                // multiple same keys, value 0, maybe after kill
                _changedParticles.Add(netParticle.Id, netParticle);
            }
        }

        private void OnParticleCollision(GameObject other)
        {
            InvokeParticleCollision(other);
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

            if (_syncFullStateOnJoin)
            {
                SendFullState(connection);
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // host check
            if (IsServer) return;

            // disable autoplay on clients, they will play when they get the data
#pragma warning disable 0618
            _particleSystem.playOnAwake = false;
#pragma warning restore 0618

            // disable collision on clients.
            // the server will handle it and send RpcKill's for client to remove them
            var col = _particleSystem.collision;
            col.enabled = false;
        }

        /// <summary>
        /// Invokes particle collision events. 
        /// Make sure you have "Send Collision Messages" enabled in the particle system's collision module.
        /// </summary>
        /// <param name="other"></param>
        private void InvokeParticleCollision(GameObject other)
        {
            OnParticleCollisionWithGameObject?.Invoke(other);
        }

        private float OneWayRTT(uint serverTick)
        {
            return (float)TimeManager.TicksToTime(TimeManager.Tick - serverTick);
        }

        /// <summary>
        /// Emits all the existing particles on the client of the given conn.
        /// </summary>
        /// <param name="conn"></param>
        private void SendFullState(NetworkConnection conn)
        {
            if (_logLevel >= LoggingType.Common)
                Debug.Log("Sending full state");

            GetParticleData();

            if (_particleCount == 0) return;

            for (int i = 0; i < _particleCount; i++)
            {
                // skip dead particles
                if (_particles[i].remainingLifetime <= 0f) continue;

#pragma warning disable 0618
                TargetEmit(conn, _particles[i].position, _particles[i].velocity,
                    _particles[i].size, _particles[i].remainingLifetime, _particles[i].color,
                    (int)_customData[i].x);
#pragma warning restore 0618
            }
        }

        /// <summary>
        /// Finds a particle by ID and kills it.
        /// </summary>
        /// <param name="id"></param>
        [ObserversRpc]
        void RpcKill(int id)
        {
            if (IsServer) return;

            Debug.Log("Killing particle ID: " + id);
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

        /// <summary>
        /// Emits a particle with parameters.
        /// </summary>
        /// <param name="pos">Particle position.</param>
        /// <param name="vel">Particle velocity.</param>
        /// <param name="size">Particle size.</param>
        /// <param name="lifetime">Particle lifetime.</param>
        /// <param name="color">Particle color.</param>
        /// <param name="id">Particle ID.</param>
        [ObserversRpc]
        void RpcEmit(Vector3 pos, Vector3 vel, float size, float lifetime, Color32 color, int id)
        {
            Emit(pos, vel, size, lifetime, color, id);
        }

        /// <summary>
        /// Emits a particle with parameters.
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="pos"></param>
        /// <param name="vel"></param>
        /// <param name="size"></param>
        /// <param name="lifetime"></param>
        /// <param name="color"></param>
        /// <param name="id"></param>
        [TargetRpc]
        void TargetEmit(NetworkConnection conn, Vector3 pos, Vector3 vel, float size, float lifetime, Color32 color, int id)
        {
            if (_logLevel >= LoggingType.Common)
                Debug.Log("Emitting at " + pos);

            Emit(pos, vel, size, lifetime, color, id);
        }

        /// <summary>
        /// Emits a particle with params and caches its data.
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="vel"></param>
        /// <param name="size"></param>
        /// <param name="lifetime"></param>
        /// <param name="color"></param>
        /// <param name="id"></param>
        [Client]
        void Emit(Vector3 pos, Vector3 vel, float size, float lifetime, Color32 color, int id)
        {
            if (IsServer) return;

            // using obsolete on purpose bc it actually works
#pragma warning disable 0618
            _particleSystem.Emit(pos, vel, size, lifetime, color);
#pragma warning restore 0618

            Debug.Log("Emitting particle at " + pos);
            GetParticleData();

            for (int i = 0; i < _particleCount; i++)
            {
                if (_logLevel >= LoggingType.Common)
                    Debug.Log($"Particle {i} empty id");

                if (_customData[i].x == 0)
                {
                    _customData[i] = new Vector4(id, 0, 0, 0);

                    if (_logLevel >= LoggingType.Common)
                        Debug.Log($"Particle index {i} NEW ID: {_customData[i].x}");

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
            return _prng.Next();
            //return Animator.StringToHash(System.Guid.NewGuid().ToString());
        }

        /// <summary>
        /// Gets the current state of the particle system particles.
        /// Returns its current particle count.
        /// </summary>
        /// <returns></returns>
        public int GetParticleData()
        {
            _particleCount = _particleSystem.GetParticles(_particles);
            _particleCache = _particles;
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
