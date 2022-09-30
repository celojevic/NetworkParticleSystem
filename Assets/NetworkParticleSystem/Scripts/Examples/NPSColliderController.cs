using FishNet.Object;
using UnityEngine;

namespace gooby.NetworkParticleSystem.Examples
{

    public class NPSColliderController : NetworkBehaviour
    {

        [SerializeField] private float _moveSpeed = 15f;
        [SerializeField] private float _rotSpeed = 30f;

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (IsOwner)
            {
                Camera.main.transform.position = transform.position + new Vector3(0, 4, -10);
                Camera.main.transform.LookAt(transform);
                Camera.main.transform.SetParent(transform);
            }
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            if (IsOwner)
            {
                Camera.main?.transform?.SetParent(null);
            }
        }

        private void Update()
        {
            if (IsOwner)
            {
                float y = Input.GetKey(KeyCode.Space) ? 1 : Input.GetKey(KeyCode.LeftControl) ? -1 : 0;
                transform.Translate(new Vector3(Input.GetAxis("Horizontal"), y, Input.GetAxis("Vertical")) * Time.deltaTime * _moveSpeed);

                float angle = Input.GetKey(KeyCode.Q) ? -1 : Input.GetKey(KeyCode.E) ? 1 : 0;
                angle *= Time.deltaTime * _rotSpeed;
                transform.Rotate(Vector3.up, angle);
            }
        }

    }

}
