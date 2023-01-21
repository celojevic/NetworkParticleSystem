using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;
using static UnityEngine.ParticleSystem;

namespace gooby.NetworkParticleSystem.Examples.FishNetSurvivors
{

    public class ExpGemParticles : NetworkBehaviour
    {

        private NetworkParticleSystem _nps;

        private void Awake()
        {
            _nps = GetComponent<NetworkParticleSystem>();
        }

        private void OnEnable()
        {
            _nps.OnParticleCollisionWithGameObject.AddListener(OnParticleCollisionWithGO);
        }

        private void OnDisable()
        {
            _nps.OnParticleCollisionWithGameObject.RemoveListener(OnParticleCollisionWithGO);
        }

        private void OnParticleCollisionWithGO(GameObject go)
        {
            if (go.TryGetComponent(out PlayerController player))
            {
                player.Coins++;
            }
        }

        public void SpawnGem(Vector2 position)
        {
            EmitParams emit = new EmitParams();
            emit.position = position;
            _nps.ParticleSystem.Emit(emit, 1);
        }

    }

}
