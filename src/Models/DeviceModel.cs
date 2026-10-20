using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using UnityEngine;

namespace SailwindRadio.Models
{
    [DataContract]
    internal sealed class ModelDocument
    {
        [DataMember]
        public int version
        {
            get; set;
        }
        [DataMember]
        public ModelMaterial[] materials
        {
            get; set;
        }
        [DataMember]
        public ModelDefinition[] models
        {
            get; set;
        }
    }
    [DataContract]
    internal sealed class ModelMaterial
    {
        [DataMember]
        public string name
        {
            get; set;
        }
        [DataMember]
        public float[] color
        {
            get; set;
        }
        [DataMember]
        public float metallic
        {
            get; set;
        }
        [DataMember]
        public float smoothness
        {
            get; set;
        }
    }
    [DataContract]
    internal sealed class ModelDefinition
    {
        [DataMember]
        public int kind
        {
            get; set;
        }
        [DataMember]
        public ModelPart[] parts
        {
            get; set;
        }
        [DataMember]
        public ModelBounds bounds
        {
            get; set;
        }
        // Pickup and physics box. It stays behind every control so the pointer reaches them.
        [DataMember]
        public ModelBounds contact
        {
            get; set;
        }
        // Optional solid boxes for placed items to rest on, where the model bounds reach above the real tops.
        [DataMember]
        public ModelBounds[] solids
        {
            get; set;
        }
        // Regional radios name where the runtime builds their title and artist rows. The models carry no display surface.
        [DataMember]
        public ModelDisplay[] displays
        {
            get; set;
        }
    }
    internal enum DisplayMechanism { SplitFlap, Drum, Scroll }
    // Metres in model-local axes. Normal points out of the visible face toward the viewer.
    [DataContract]
    internal sealed class ModelDisplay
    {
        [DataMember]
        public string type { get; set; }
        // Parsed from type when the model data is validated.
        internal DisplayMechanism Mechanism { get; set; }
        [DataMember]
        public string row { get; set; }
        [DataMember]
        public float[] center { get; set; }
        [DataMember]
        public float[] right { get; set; }
        [DataMember]
        public float[] up { get; set; }
        [DataMember]
        public float[] normal { get; set; }
        [DataMember]
        public float width { get; set; }
        [DataMember]
        public float height { get; set; }
        // Split-flap and drum rows only.
        [DataMember]
        public int cells { get; set; }
        [DataMember]
        public float pitch { get; set; }
        // Drum rows only. The centre already lies on the drum front surface.
        [DataMember]
        public float radius { get; set; }
    }
    [DataContract]
    internal sealed class ModelBounds
    {
        [DataMember]
        public float[] min
        {
            get; set;
        }
        [DataMember]
        public float[] max
        {
            get; set;
        }
    }
    [DataContract]
    internal sealed class ModelPart
    {
        [DataMember]
        public float[] pivot { get; set; }
        // Knob rotation axis in model space, pointing out of the face the knob sits on.
        [DataMember]
        public float[] axis { get; set; }
        [DataMember]
        public string name
        {
            get; set;
        }
        [DataMember]
        public int material
        {
            get; set;
        }
        [DataMember]
        public float[] vertices
        {
            get; set;
        }
        [DataMember]
        public float[] normals
        {
            get; set;
        }
        [DataMember]
        public int[] triangles
        {
            get; set;
        }
    }

    internal static class DeviceModel
    {
        private static ModelDocument document;
        private static int[] kinds;

        // Version 1 holds exactly the walnut set. Version 2 adds any regional kinds, which need bounds.
        internal static ModelDocument Read(Stream stream)
        {
            var data = (ModelDocument)new DataContractJsonSerializer(typeof(ModelDocument)).ReadObject(stream);
            if (data == null || (data.version != 1 && data.version != 2) || data.models == null || data.materials == null ||
                (data.version == 1 ? data.models.Length != 4 || data.materials.Length > 32 : data.models.Length > DeviceKind.Count || data.materials.Length > 64))
                throw new InvalidDataException("Unsupported radio model data");
            var kinds = new HashSet<int>();
            foreach (var model in data.models)
            {
                if (model == null || !DeviceKind.IsValid(model.kind) || (data.version == 1 && !DeviceKind.IsWalnut(model.kind)) || !kinds.Add(model.kind) ||
                    model.parts == null || model.parts.Length > 64)
                    throw new InvalidDataException("Invalid radio model");
                if (model.bounds != null ? !ValidBounds(model.bounds) : !DeviceKind.IsWalnut(model.kind))
                    throw new InvalidDataException("Invalid radio model bounds");
                if (model.contact != null ? !ValidBounds(model.contact) : !DeviceKind.IsWalnut(model.kind))
                    throw new InvalidDataException("Invalid radio contact box");
                if (model.solids != null && (DeviceKind.IsWalnut(model.kind) || model.solids.Length == 0 || model.solids.Length > 32 ||
                    !Array.TrueForAll(model.solids, box => box != null && ValidBounds(box) && Inside(box, model.bounds))))
                    throw new InvalidDataException("Invalid radio solid boxes");
                bool regionalRadio = !DeviceKind.IsWalnut(model.kind) && DeviceKind.Role(model.kind) == DeviceRole.Radio;
                if (model.displays != null ? !regionalRadio || !ValidDisplays(model.displays) : regionalRadio)
                    throw new InvalidDataException("Invalid radio display anchors");
                var named = new Dictionary<string, ModelPart>(StringComparer.Ordinal);
                foreach (var part in model.parts)
                {
                    if (part == null || string.IsNullOrEmpty(part.name) || part.material < 0 || part.material >= data.materials.Length ||
                        part.vertices == null || part.normals == null || part.triangles == null || part.vertices.Length != part.normals.Length ||
                        part.vertices.Length % 3 != 0 || part.vertices.Length > 180000 || part.triangles.Length % 3 != 0)
                        throw new InvalidDataException("Invalid radio mesh");
                    if (part.pivot == null || part.pivot.Length != 3)
                        throw new InvalidDataException("Invalid radio part pivot");
                    foreach (float value in part.pivot)
                        if (float.IsNaN(value) || float.IsInfinity(value))
                            throw new InvalidDataException("Invalid radio part pivot");
                    foreach (float value in part.vertices)
                    if (float.IsNaN(value) || float.IsInfinity(value))
                        throw new InvalidDataException("Invalid radio vertex");
                    foreach (float value in part.normals)
                    if (float.IsNaN(value) || float.IsInfinity(value))
                        throw new InvalidDataException("Invalid radio normal");
                    foreach (int index in part.triangles)
                    if (index < 0 || index >= part.vertices.Length / 3)
                        throw new InvalidDataException("Invalid radio triangle");
                    if (part.axis != null && !ValidAxis(part.axis))
                        throw new InvalidDataException("Invalid radio part axis");
                    // A named part may span several materials. Its pieces move as one object.
                    if (part.name != "body" && named.TryGetValue(part.name, out var first) &&
                        (HasOwnLightMaterial(part.name) || !SameVector(first.pivot, part.pivot) || !SameVector(first.axis, part.axis)))
                        throw new InvalidDataException("Invalid repeated radio part");
                    named[part.name] = part;
                }
            }
            for (int kind = 0; kind < 4; kind++)
                if (!kinds.Contains(kind))
                    throw new InvalidDataException("Radio model kind is missing");
            return data;
        }

        private static bool Finite(float[] values)
        {
            if (values == null || values.Length != 3)
                return false;
            foreach (float value in values)
                if (float.IsNaN(value) || float.IsInfinity(value))
                    return false;
            return true;
        }
        private static bool ValidAxis(float[] axis) => Finite(axis) && axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2] > 1e-6f;
        private static bool ValidBounds(ModelBounds bounds) => Finite(bounds.min) && Finite(bounds.max) &&
            bounds.min[0] < bounds.max[0] && bounds.min[1] < bounds.max[1] && bounds.min[2] < bounds.max[2];
        // Within the outer box, allowing a millimetre of export rounding.
        private static bool Inside(ModelBounds box, ModelBounds outer)
        {
            for (int a = 0; a < 3; a++)
                if (box.min[a] < outer.min[a] - .001f || box.max[a] > outer.max[a] + .001f)
                    return false;
            return true;
        }
        private static bool SameVector(float[] a, float[] b) => a == null ? b == null :
            b != null && a[0] == b[0] && a[1] == b[1] && a[2] == b[2];

        // One title and one artist row. Each frame is orthonormal and right-handed as seen from the
        // front, so LookRotation(-normal, up) lays text along right.
        private static bool ValidDisplays(ModelDisplay[] displays)
        {
            var rows = new HashSet<string>(StringComparer.Ordinal);
            foreach (var display in displays)
            {
                if (display == null || (display.row != "title" && display.row != "artist") || !rows.Add(display.row) ||
                    !Finite(display.center) || !Unit(display.right) || !Unit(display.up) || !Unit(display.normal) ||
                    !Positive(display.width) || !Positive(display.height))
                    return false;
                Vector3 right = Vector(display.right), up = Vector(display.up), normal = Vector(display.normal);
                if (Mathf.Abs(Vector3.Dot(right, up)) > 1e-3f || Mathf.Abs(Vector3.Dot(right, normal)) > 1e-3f ||
                    Mathf.Abs(Vector3.Dot(up, normal)) > 1e-3f || Vector3.Dot(Vector3.Cross(up, -normal), right) < .999f)
                    return false;
                switch (display.type)
                {
                    case "splitflap": display.Mechanism = DisplayMechanism.SplitFlap; break;
                    case "drum": display.Mechanism = DisplayMechanism.Drum; break;
                    case "scroll": display.Mechanism = DisplayMechanism.Scroll; break;
                    default: return false;
                }
                bool cells = display.Mechanism != DisplayMechanism.Scroll;
                if (cells && (display.cells < 1 || display.cells > 64 || !Positive(display.pitch)))
                    return false;
                if (display.Mechanism == DisplayMechanism.Drum && !Positive(display.radius))
                    return false;
                // RegionalTrackDisplay builds both rows with the first row's mechanism.
                if (display.Mechanism != displays[0].Mechanism)
                    return false;
            }
            return rows.Count == 2;
        }
        private static bool Positive(float value) => value > 0 && !float.IsInfinity(value);
        private static bool Unit(float[] values) => Finite(values) && Mathf.Abs(Vector(values).magnitude - 1) < 1e-3f;
        private static Vector3 Vector(float[] values) => new Vector3(values[0], values[1], values[2]);

        private static ModelDocument Document
        {
            get
            {
                if (document == null)
                    using (var stream = typeof(DeviceModel).Assembly.GetManifestResourceStream("SailwindRadio.Models.devices.json"))
                    {
                        if (stream == null)
                            throw new InvalidDataException("Radio model resource is missing");
                        var read = Read(stream);
                        var present = new List<int>();
                        foreach (var model in read.models)
                            present.Add(model.kind);
                        present.Sort();
                        kinds = present.ToArray();
                        document = read;
                    }
                return document;
            }
        }

        private static ModelDefinition Definition(int kind)
        {
            foreach (var model in Document.models)
                if (model.kind == kind)
                    return model;
            return null;
        }

        // Ascending kinds present in the embedded document. Only these are registered as items.
        internal static IReadOnlyList<int> Kinds
        {
            get
            {
                _ = Document;
                return kinds;
            }
        }

        internal static bool TryGetBounds(int kind, out Vector3 min, out Vector3 max) => Box(Definition(kind)?.bounds, out min, out max);
        internal static bool TryGetContact(int kind, out Vector3 min, out Vector3 max) => Box(Definition(kind)?.contact, out min, out max);
        // Each authored solid box as its min and max corners, empty when the model has none.
        internal static IReadOnlyList<ModelBounds> Solids(int kind) => Definition(kind)?.solids ?? Array.Empty<ModelBounds>();
        internal static IReadOnlyList<ModelDisplay> Displays(int kind) => Definition(kind)?.displays ?? Array.Empty<ModelDisplay>();
        private static bool Box(ModelBounds box, out Vector3 min, out Vector3 max)
        {
            min = box == null ? Vector3.zero : new Vector3(box.min[0], box.min[1], box.min[2]);
            max = box == null ? Vector3.zero : new Vector3(box.max[0], box.max[1], box.max[2]);
            return box != null;
        }

        internal static bool TryGetAxis(int kind, string part, out Vector3 axis)
        {
            axis = Vector3.zero;
            var model = Definition(kind);
            if (model != null)
                foreach (var candidate in model.parts)
                    if (candidate.name == part && candidate.axis != null)
                    {
                        axis = new Vector3(candidate.axis[0], candidate.axis[1], candidate.axis[2]).normalized;
                        return true;
                    }
            return false;
        }

        internal static bool HasOwnLightMaterial(string name)
        {
            if (name == "screen")
                return true;
            string key = name.StartsWith("icon_", StringComparison.Ordinal) ? name.Substring(5) : "";
            return key == "power" || key == "playpause" || key == "shuffle" ||
                key == "previous" || key == "next" || key == "collections";
        }

        internal static Dictionary<string, GameObject> Build(GameObject root, int kind, List<UnityEngine.Object> owned)
        {
            var document = Document;
            var selected = Definition(kind);
            if (selected == null)
                throw new InvalidDataException("Radio model kind is missing");
            var shader = Shader.Find("Standard");
            if (!shader)
                shader = root.GetComponent<Renderer>().sharedMaterial.shader;
            // The document holds every device family. Resolve this device's materials on first use,
            // sharing repeated indices while leaving unrelated family materials uninstantiated.
            var material = SharedMaterialResolver<Material>(i =>
            {
                var source = document.materials[i];
                return Surface(shader, source.name, new Color(source.color[0], source.color[1], source.color[2], source.color[3]),
                    source.metallic, source.smoothness, owned);
            });
            var objects = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            var bodyMeshes = new List<CombineInstance>();
            var bodyMaterials = new List<Material>();
            foreach (var part in selected.parts)
            {
                var mesh = new Mesh { name = part.name };
                var vertices = new Vector3[part.vertices.Length / 3];
                var normals = new Vector3[vertices.Length];
                var pivot = new Vector3(part.pivot[0], part.pivot[1], part.pivot[2]);
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = new Vector3(part.vertices[i * 3], part.vertices[i * 3 + 1], part.vertices[i * 3 + 2]) - pivot;
                    normals[i] = new Vector3(part.normals[i * 3], part.normals[i * 3 + 1], part.normals[i * 3 + 2]);
                }
                mesh.vertices = vertices;
                mesh.normals = normals;
                mesh.triangles = part.triangles;
                mesh.RecalculateBounds();
                owned.Add(mesh);
                var sharedMaterial = material(part.material);
                if (part.name == "body")
                {
                    bodyMeshes.Add(new CombineInstance { mesh = mesh, transform = Matrix4x4.identity });
                    bodyMaterials.Add(sharedMaterial);
                    continue;
                }
                if (objects.TryGetValue(part.name, out var existing))
                {
                    AddSection(existing, mesh, sharedMaterial, owned);
                    continue;
                }
                var child = new GameObject(part.name);
                child.transform.SetParent(root.transform, false);
                child.transform.localPosition = pivot;
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = child.AddComponent<MeshRenderer>();
                if (HasOwnLightMaterial(part.name))
                {
                    var glow = new Material(sharedMaterial);
                    glow.EnableKeyword("_EMISSION");
                    glow.SetColor("_EmissionColor", Color.black);
                    renderer.sharedMaterial = glow;
                    owned.Add(glow);
                }
                else
                    renderer.sharedMaterial = sharedMaterial;
                if (part.name.StartsWith("control_", StringComparison.Ordinal))
                {
                    var collider = child.AddComponent<BoxCollider>();
                    FitControl(collider, mesh);
                    collider.isTrigger = true;
                }
                objects.Add(part.name, child);
            }
            // Indicator and knob share an authored pivot. Keep legends and body fixed.
            foreach (var pair in objects)
            {
                if (!pair.Key.StartsWith("indicator_", StringComparison.Ordinal))
                    continue;
                if (!objects.TryGetValue("control_" + pair.Key.Substring(10), out var knob))
                    throw new InvalidDataException("Radio knob indicator has no control");
                pair.Value.transform.SetParent(knob.transform, false);
                pair.Value.transform.localPosition = Vector3.zero;
            }
            var body = new Mesh { name = "Radio device body" };
            body.CombineMeshes(bodyMeshes.ToArray(), false, false);
            owned.Add(body);
            root.GetComponent<MeshFilter>().sharedMesh = body;
            root.GetComponent<Renderer>().sharedMaterials = bodyMaterials.ToArray();
            return objects;
        }

        internal static Func<int, T> SharedMaterialResolver<T>(Func<int, T> create)
        {
            var materials = new Dictionary<int, T>();
            return index =>
            {
                if (!materials.TryGetValue(index, out var material))
                {
                    material = create(index);
                    materials.Add(index, material);
                }
                return material;
            };
        }

        // Colours are sRGB like devices.json. Unity converts material colours for the linear player.
        internal static Material Surface(Shader shader, string name, Color color, float metallic, float smoothness, List<UnityEngine.Object> owned)
        {
            var material = new Material(shader) { name = name, color = color };
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Glossiness", smoothness);
            // Reflection probes can show the moon even where scene geometry
            // blocks it. Retain direct-lit metal highlights without that
            // unoccluded environment contribution on portable devices.
            if (shader.name == "Standard")
            {
                material.SetFloat("_GlossyReflections", 0f);
                material.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
            }
            owned.Add(material);
            return material;
        }

        private static void FitControl(BoxCollider collider, Mesh mesh)
        {
            collider.center = mesh.bounds.center;
            // A little room around the visible cap helps native pointer
            // raycasts at sea without allowing adjacent controls to meet.
            collider.size = mesh.bounds.size + new Vector3(.012f, .012f, .018f);
        }

        // A repeated part name is one control authored in several materials. Keep it one object
        // with one submesh per material so it moves, lights and collides as a single control.
        private static void AddSection(GameObject part, Mesh section, Material material, List<UnityEngine.Object> owned)
        {
            var filter = part.GetComponent<MeshFilter>();
            var pieces = new List<CombineInstance>();
            for (int i = 0; i < filter.sharedMesh.subMeshCount; i++)
                pieces.Add(new CombineInstance { mesh = filter.sharedMesh, subMeshIndex = i, transform = Matrix4x4.identity });
            pieces.Add(new CombineInstance { mesh = section, transform = Matrix4x4.identity });
            var combined = new Mesh { name = part.name };
            combined.CombineMeshes(pieces.ToArray(), false, false);
            owned.Add(combined);
            filter.sharedMesh = combined;
            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = new List<Material>(renderer.sharedMaterials) { material }.ToArray();
            var collider = part.GetComponent<BoxCollider>();
            if (collider)
                FitControl(collider, combined);
        }
    }
}
