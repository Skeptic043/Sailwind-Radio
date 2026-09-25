using SailwindRadio.Persistence;
using UnityEngine;

namespace SailwindRadio.Physical
{
    // Added to each off-scene directory asset during registration. A normal Instantiate
    // runs Awake on the clone, so callers need only the native item methods.
    [DefaultExecutionOrder(100)]
    public sealed class RadioTemplateMarker : MonoBehaviour
    {
        [SerializeField] private int kind = -1;

        internal int Kind => kind;
        internal void Configure(int value) { kind = value; }

        private void Awake()
        {
            if (kind < 0 || kind > 3 || !gameObject.scene.IsValid() ||
                !RadioWorldService.IsTemplateInstanceName(gameObject.name, kind)) return;
            var prefab = GetComponent<SaveablePrefab>();
            if (!prefab || prefab.prefabIndex != RadioSaveStore.ItemIndex(kind)) return;
            RadioWorldService.AttachTemplateClone(this);
        }
    }
}
