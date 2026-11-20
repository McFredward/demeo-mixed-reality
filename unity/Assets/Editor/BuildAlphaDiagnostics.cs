using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildAlphaDiagnostics
{
    public static void Build()
    {
        string output = Path.GetFullPath(Path.Combine(Application.dataPath,"../../assets"));
        Directory.CreateDirectory(output);
#pragma warning disable 618
        PlayerSettings.virtualRealitySupported = true;
        PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing;
#pragma warning restore 618
        var build = new AssetBundleBuild { assetBundleName = "alphadiagnostics.bundle", assetNames = new[] {"Assets/AlphaDiagnostics.shader"} };
        if (BuildPipeline.BuildAssetBundles(output,new[] {build},BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64) == null) throw new BuildFailedException("Alpha diagnostic bundle build failed");
        var bundle = AssetBundle.LoadFromMemory(File.ReadAllBytes(Path.Combine(output,"alphadiagnostics.bundle")));
        if (bundle == null) throw new BuildFailedException("Diagnostic bundle failed native load");
        try
        {
            var names = bundle.GetAllAssetNames();
            if (names.Length != 1 || names[0] != "assets/alphadiagnostics.shader") throw new BuildFailedException("Diagnostic asset directory mismatch");
            var shader = bundle.LoadAsset<Shader>(names[0]);
            if (shader == null || shader.name != "Hidden/Demeo/AlphaDiagnostics") throw new BuildFailedException("Diagnostic shader missing");
            Debug.Log("Alpha diagnostic bundle native load verified");
        }
        finally { bundle.Unload(true); }
    }
}
public sealed class AlphaDiagnosticVariants : IPreprocessShaders
{
    public int callbackOrder => int.MaxValue;
    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (shader.name != "Hidden/Demeo/AlphaDiagnostics") return;
        bool mono=false,stereo=false;
        var key=new ShaderKeyword("STEREO_INSTANCING_ON");
        foreach (var v in data) if (v.shaderKeywordSet.IsEnabled(key)) stereo=true; else mono=true;
        if (!mono || !stereo) throw new BuildFailedException("Diagnostic shader requires mono and stereo-instanced variants");
    }
}
