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
    }
    [DataContract]
    internal sealed class ModelPart
    {
        [DataMember]
        public float[] pivot { get; set; }
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

        internal static ModelDocument Read(Stream stream)
        {
            var data = (ModelDocument)new DataContractJsonSerializer(typeof(ModelDocument)).ReadObject(stream);
            if (data == null || data.version != 1 || data.models == null || data.models.Length != 4 || data.materials == null || data.materials.Length > 32)
                throw new InvalidDataException("Unsupported radio model data");
            var kinds = new HashSet<int>();
            foreach (var model in data.models)
            {
                if (model == null || model.kind < 0 || model.kind > 3 || !kinds.Add(model.kind) || model.parts == null || model.parts.Length > 64)
                    throw new InvalidDataException("Invalid radio model");
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
                }
            }
            return data;
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
            if (document == null)
                using (var stream = typeof(DeviceModel).Assembly.GetManifestResourceStream("SailwindRadio.Models.devices.json"))
                {
                    if (stream == null)
                        throw new InvalidDataException("Radio model resource is missing");
                    document = Read(stream);
                }
            ModelDefinition selected = null;
            foreach (var model in document.models)
            if (model.kind == kind)
                selected = model;
            if (selected == null)
                throw new InvalidDataException("Radio model kind is missing");
            var shader = Shader.Find("Standard");
            if (!shader)
                shader = root.GetComponent<Renderer>().sharedMaterial.shader;
            var materials = new Material[document.materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                var source = document.materials[i];
                var material = new Material(shader) { name = source.name, color = new Color(source.color[0], source.color[1], source.color[2], source.color[3]) };
                material.SetFloat("_Metallic", source.metallic);
                material.SetFloat("_Glossiness", source.smoothness);
                materials[i] = material;
                owned.Add(material);
            }
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
                if (part.name == "body")
                {
                    bodyMeshes.Add(new CombineInstance { mesh = mesh, transform = Matrix4x4.identity });
                    bodyMaterials.Add(materials[part.material]);
                    continue;
                }
                var child = new GameObject(part.name);
                child.transform.SetParent(root.transform, false);
                child.transform.localPosition = pivot;
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = child.AddComponent<MeshRenderer>();
                if (HasOwnLightMaterial(part.name))
                {
                    var glow = new Material(materials[part.material]);
                    glow.EnableKeyword("_EMISSION");
                    glow.SetColor("_EmissionColor", Color.black);
                    renderer.sharedMaterial = glow;
                    owned.Add(glow);
                }
                else
                    renderer.sharedMaterial = materials[part.material];
                if (part.name.StartsWith("control_", StringComparison.Ordinal))
                {
                    var collider = child.AddComponent<BoxCollider>();
                    collider.center = mesh.bounds.center;
                    collider.size = mesh.bounds.size + new Vector3(.002f, .002f, .012f);
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
    }
}
