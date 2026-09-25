using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Runs in Unity 2019.1.10f1. The bundle contains only ordinary Unity components.
// Sailwind's native item components are added to these off-scene assets at runtime.
public static class BuildRadioItems
{
    public static void Build()
    {
        const string assetRoot = "Assets/RadioItems";
        Directory.CreateDirectory(assetRoot);
        var mesh = new Mesh { name = "Radio item template mesh" };
        mesh.vertices = new[] {
            new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f),
            new Vector3(.5f,.5f,-.5f), new Vector3(-.5f,.5f,-.5f),
            new Vector3(-.5f,-.5f,.5f), new Vector3(.5f,-.5f,.5f),
            new Vector3(.5f,.5f,.5f), new Vector3(-.5f,.5f,.5f)
        };
        mesh.triangles = new[] {
            0,2,1, 0,3,2, 4,5,6, 4,6,7,
            0,1,5, 0,5,4, 3,7,6, 3,6,2,
            1,2,6, 1,6,5, 0,4,7, 0,7,3
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        AssetDatabase.CreateAsset(mesh, assetRoot + "/TemplateMesh.asset");
        var shader = Shader.Find("Standard");
        if (!shader) throw new InvalidOperationException("Standard shader unavailable");
        var material = new Material(shader) { name = "Radio item template material" };
        AssetDatabase.CreateAsset(material, assetRoot + "/TemplateMaterial.mat");
        for (int kind = 0; kind < 4; kind++)
        {
            int index = 43040 + kind;
            var root = new GameObject("Sailwind Radio item " + index);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            root.AddComponent<MeshRenderer>().sharedMaterial = material;
            root.AddComponent<BoxCollider>();
            var body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            var path = assetRoot + "/RadioItem" + index + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            AssetImporter.GetAtPath(path).assetBundleName = "radio-items.assets";
        }
        AssetDatabase.SaveAssets();
        Directory.CreateDirectory("Build");
        BuildPipeline.BuildAssetBundles("Build", BuildAssetBundleOptions.UncompressedAssetBundle, BuildTarget.StandaloneWindows64);
        if (!File.Exists("Build/radio-items.assets"))
            throw new FileNotFoundException("Radio item bundle was not built");
        var bundle = AssetBundle.LoadFromMemory(File.ReadAllBytes("Build/radio-items.assets"));
        if (!bundle) throw new InvalidDataException("Radio item bundle cannot be loaded");
        for (int kind = 0; kind < 4; kind++)
        {
            int index = 43040 + kind;
            var asset = bundle.LoadAsset<GameObject>("assets/radioitems/radioitem" + index + ".prefab");
            if (!asset || asset.scene.IsValid() || asset.transform.localScale != Vector3.one ||
                !asset.GetComponent<Rigidbody>() || !asset.GetComponent<BoxCollider>() ||
                !asset.GetComponent<MeshFilter>().sharedMesh ||
                !asset.GetComponent<MeshRenderer>().enabled ||
                !asset.GetComponent<MeshRenderer>().sharedMaterial ||
                !asset.GetComponent<MeshRenderer>().sharedMaterial.shader)
                throw new InvalidDataException("Radio item asset failed native discovery preflight: " + index);
            var added = asset.AddComponent<AudioSource>();
            if (!added || asset.scene.IsValid())
                throw new InvalidDataException("Off-scene item asset rejected a runtime component: " + index);
            var clone = UnityEngine.Object.Instantiate(asset);
            if (!clone || !clone.scene.IsValid() || !clone.GetComponent<AudioSource>())
                throw new InvalidDataException("Radio item clone did not enter a scene: " + index);
            UnityEngine.Object.DestroyImmediate(clone);
        }
        bundle.Unload(false);
        Debug.Log("Radio item bundle built for Unity 2019.1.10f1");
    }
}
