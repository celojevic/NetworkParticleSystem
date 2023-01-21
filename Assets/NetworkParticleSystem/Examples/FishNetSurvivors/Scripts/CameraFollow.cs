using System.Collections.Generic;
using UnityEngine;

namespace gooby.NetworkParticleSystem.Examples.FishNetSurvivors
{
    public class CameraFollow : MonoBehaviour
    {
        public Transform Target;

        private void LateUpdate()
        {
            if (Target != null)
                transform.position = new Vector3(Target.position.x, Target.position.y, -10);
        }
    }
}