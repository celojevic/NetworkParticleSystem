using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace gooby.NetworkParticleSystem.Examples.FishNetSurvivors
{

    public class PlayerController : NetworkBehaviour
    {

        [SyncVar]
        public int Coins;

        [SerializeField] private float _moveSpeed = 3f;

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (IsOwner)
            {
                Camera.main.GetComponent<CameraFollow>().Target = transform;
            }
        }

        private void Update()
        {
            if (!IsOwner) return;

            Vector3 move = new Vector3(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            transform.Translate(move.normalized * Time.deltaTime * _moveSpeed);
        }

    }

}
