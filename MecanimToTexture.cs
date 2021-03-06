#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.IO;
using UnityEngine.Experimental.Rendering;
using Unity.EditorCoroutines.Editor;

public class MecanimToTexture : EditorWindow
{
    private readonly string[] TabTitles = new string[] {
        "Animation Baker",
        "Mesh Baker",
        "UV Mapper"
    };
    private int currentTab;
    private Vector2 scrollPosition = Vector2.zero;

    #region Animation Texture Props
    private GameObject animationRigObject;
    private BakeMode bakeMode = BakeMode.AllIndividual;
    private int framesPerSecondCapture = 30;
    private int clipToBakeIndex = 0;
    private bool bakeRotation = true;
    private float animationTextureScaler = 1;
    private int minFrame = 0;
    private int maxFrame = 200;
    private ColorMode animationTextureColorMode = ColorMode.HDR;
    #endregion

    #region Mesh Texture Props
    private Mesh textureMesh;
    private ColorMode meshTextureColorMode = ColorMode.HDR;
    private float meshTextureScaler = 1;
    #endregion

    #region UV Map Props
    private Mesh uvMesh;
    private UVLayer uvLayer = UVLayer.UV1;
    private float uvMeshScale = 1;
    #endregion

    private List<string> animationTextureErrors = new List<string>();
    private List<string> meshTextureErrors = new List<string>();
    private List<string> uvErrors = new List<string>();

    [MenuItem("Window/ifelse/Mecanim2Texture")]
    private static void Init()
    {
        GetWindow(typeof(MecanimToTexture), false, "Mecanim2Texture");
    }

    private void OnGUI()
    {
        GUI.skin.label.wordWrap = true;
        EditorGUILayout.Space(EditorGUIUtility.singleLineHeight);
        currentTab = GUILayout.Toolbar(currentTab, TabTitles);

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        switch (currentTab)
        {
            case 0:
                AnimationTextureEditor();
                break;
            case 1:
                MeshTextureEditor();
                break;
            case 2:
                UVMapEditor();
                break;
        }
        EditorGUILayout.EndScrollView();

        switch (currentTab)
        {
            case 0:
                ErrorView(animationTextureErrors);
                break;
            case 1:
                ErrorView(meshTextureErrors);
                break;
            case 2:
                ErrorView(uvErrors);
                break;
        }
    }

    #region Animation Texture
    private void AnimationTextureEditor()
    {
        animationRigObject = (GameObject)EditorGUILayout.ObjectField("Animation Rig", animationRigObject, typeof(GameObject), true);

        if (animationRigObject == null)
        {
            return;
        }

        bool checkNextError = !SetError(Errors.MissingRigObject, animationTextureErrors, animationRigObject == null);
        if (checkNextError)
        {
            checkNextError = !SetError(Errors.MissingSkinnedMeshRenderer, animationTextureErrors, animationRigObject.GetComponentInChildren<SkinnedMeshRenderer>() == null);
        }
        if (checkNextError)
        {
            checkNextError = !SetError(Errors.MissingAnimator, animationTextureErrors, animationRigObject.GetComponentInChildren<Animator>() == null);
        }
        if (checkNextError)
        {
            checkNextError = !SetError(Errors.MissingRuntimeAnimatorController, animationTextureErrors, animationRigObject.GetComponentInChildren<Animator>().runtimeAnimatorController == null);
        }

        AnimationClip[] animationClips = animationRigObject?.GetComponentInChildren<Animator>()?.runtimeAnimatorController?.animationClips;
        if (checkNextError)
        {
            checkNextError = !SetError(Errors.NoAnimationClips, animationTextureErrors, animationClips.Length == 0);
        }
        if (!checkNextError)
        {
            return;
        }
        animationTextureColorMode = (ColorMode)EditorGUILayout.EnumPopup("Color Mode", animationTextureColorMode);
        bakeMode = (BakeMode)EditorGUILayout.EnumPopup(new GUIContent("Bake Mode", "Bake mode for faster iteration"), bakeMode);
        framesPerSecondCapture = EditorGUILayout.IntSlider(new GUIContent("FPS Capture", "How many frames per second the clip will be captured at."), framesPerSecondCapture, 1, 120);
        bakeRotation = EditorGUILayout.Toggle(new GUIContent("Bake Rotation", "Apply the game object's rotation to the baked texture"), bakeRotation);
        animationTextureScaler = EditorGUILayout.FloatField(new GUIContent("Bake Scale", "Scale the mesh before baking to reduce the chance of baked pixels being out of range."), animationTextureScaler);

        int vertexCount = animationRigObject.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh.vertexCount;
        float totalTime = 0;
        int totalFrames;
        int squareFrames;
        Vector2Int textureSize = Vector2Int.zero;
        Vector2Int minTextureSize = new Vector2Int(8192, 8192);
        Vector2Int[] textureSizes = new Vector2Int[animationClips.Length];

        AnimationClip clipToBake = null;

        int highestPOT = 0;
        switch (bakeMode)
        {
            case BakeMode.Single:
                clipToBakeIndex = EditorGUILayout.Popup(new GUIContent("Clip to Bake", "Which animation clip will be baked."), clipToBakeIndex, GetAnimationClipNames(animationClips));
                clipToBake = animationClips[clipToBakeIndex];

                totalTime = clipToBake.length;

                minFrame = EditorGUILayout.IntSlider(new GUIContent("Min Capture Frame", "The frame to start capturing at."), minFrame, 0, maxFrame);
                maxFrame = EditorGUILayout.IntSlider(new GUIContent("Max Capture Frame", "The frame to end capturing at."), maxFrame, minFrame, (int)(totalTime * framesPerSecondCapture));

                totalFrames = maxFrame - minFrame;
                int pixelCountSingle = totalFrames * vertexCount;
                squareFrames = Mathf.FloorToInt(Mathf.Sqrt(pixelCountSingle));
                int potSingle = Mathf.NextPowerOfTwo(squareFrames);
                Vector2Int optimizedSquareFramesSingle = new Vector2Int(potSingle, potSingle);
                if ((potSingle * potSingle) / 2 > pixelCountSingle)
                {
                    optimizedSquareFramesSingle.x /= 2;
                }
                textureSize = optimizedSquareFramesSingle;
                break;
            default:
                for (int i = 0; i < animationClips.Length; i++)
                {
                    float clipTime = animationClips[i].length;
                    float clipFrames = (int)(clipTime * framesPerSecondCapture);
                    totalTime += clipTime;

                    int pixelCount = (int)(clipFrames * vertexCount);
                    squareFrames = Mathf.FloorToInt(Mathf.Sqrt(pixelCount));
                    int pot = Mathf.NextPowerOfTwo(squareFrames);
                    Vector2Int optimizedSquareFrames = new Vector2Int(pot, pot);

                    highestPOT = Mathf.Max(pot, highestPOT);

                    if ((pot * pot) / 2 > pixelCount)
                    {
                        optimizedSquareFrames.x /= 2;
                    }
                    minTextureSize = Vector2Int.Min(optimizedSquareFrames, minTextureSize);
                    textureSize = Vector2Int.Max(optimizedSquareFrames, textureSize);

                    textureSizes[i] = optimizedSquareFrames;
                }
                totalFrames = (int)(totalTime * framesPerSecondCapture);
                break;
        }

        EditorGUILayout.Space(EditorGUIUtility.singleLineHeight);
        EditorGUILayout.LabelField($"Animations: {(bakeMode != BakeMode.Single ? animationClips.Length : 1)}");
        EditorGUILayout.LabelField($"Frames to bake: {totalFrames}");
        EditorGUILayout.LabelField($"Pixels to fill: {vertexCount * totalFrames}");
        switch (bakeMode)
        {
            case BakeMode.Single:
                EditorGUILayout.LabelField($"Result texture size: {textureSize}");
                break;
            case BakeMode.AllIndividual:
                EditorGUILayout.LabelField($"Result texture size: {minTextureSize} (min), {textureSize} (max)");
                break;
            case BakeMode.AllTexture2DArray:
                EditorGUILayout.LabelField($"Result texture size: {textureSize}x{animationClips.Length}");
                break;
        }
        EditorGUILayout.LabelField($"Estimated bake time: {totalTime / (60 / framesPerSecondCapture)} seconds");

        Quaternion rotation = bakeRotation ? Quaternion.Inverse(animationRigObject.transform.GetChild(0).localRotation) : Quaternion.identity;

        switch (bakeMode)
        {
            case BakeMode.Single:
                if (GUILayout.Button("Bake Animation (Single)"))
                {
                    EditorCoroutineUtility.StartCoroutine(CreateAnimationTexture(textureSize, vertexCount, totalFrames, framesPerSecondCapture, rotation, clipToBake), this);
                }
                break;
            case BakeMode.AllIndividual:
                if (GUILayout.Button("Bake Animations (Individual)"))
                {
                    EditorCoroutineUtility.StartCoroutine(CreateAnimationTextureIndividual(textureSizes, vertexCount, framesPerSecondCapture, rotation, animationClips), this);
                }
                break;
            case BakeMode.AllTexture2DArray:
                if (GUILayout.Button("Bake Animations (Texture2DArray)"))
                {
                    EditorCoroutineUtility.StartCoroutine(CreateAnimationTextureArray(highestPOT, vertexCount, framesPerSecondCapture, rotation, animationClips), this);
                }
                break;
        }
    }

    private string[] GetAnimationClipNames(AnimationClip[] clips)
    {
        string[] result = new string[clips.Length];
        for (int i = 0; i < clips.Length; i++)
        {
            result[i] = clips[i].name;
        }
        return result;
    }

    private IEnumerator CreateAnimationTextureArray(int size, int vertexCount, int fps, Quaternion rotation, AnimationClip[] clips)
    {
        string path = EditorUtility.SaveFilePanelInProject("Save Baked Animation", animationRigObject.name, "asset", "Please save your baked animation");
        if (path.Length == 0)
        {
            yield break;
        }

        GameObject prefab = Instantiate(animationRigObject);
        Animator animator = prefab.GetComponentInChildren<Animator>();
        SkinnedMeshRenderer skinnedMesh = prefab.GetComponentInChildren<SkinnedMeshRenderer>();

        Texture2DArray result = new Texture2DArray(size, size, clips.Length, TextureFormat.RGB9e5Float, false, true);
        AssetDatabase.CreateAsset(result, path);

        for (int clip = 0; clip < clips.Length; clip++)
        {
            Color[] clipColors = new Color[size * size];
            for (int i = 0; i < clipColors.Length; i++)
            {
                clipColors[i] = Color.clear;
            }

            float clipTime = clips[clip].length;
            int frames = (int)(clipTime * framesPerSecondCapture);

            animator.Play(clips[clip].name, 0, 0);
            yield return null;
            animator.Update(0);

            float animationDeltaTime = 1f / fps;

            int y = 0;
            int x = 0;
            for (int i = 0; i < frames; i++)
            {
                Mesh meshFrame = new Mesh();
                skinnedMesh.BakeMesh(meshFrame);
                meshFrame.RecalculateBounds();

                //red = x
                //green = y
                //blue = z
                for (int j = 0; j < vertexCount; j++)
                {
                    Color pixel = Color.clear;
                    Vector3 position = rotation * meshFrame.vertices[j];
                    position = position * animationTextureScaler + Vector3.one * 0.5f;
                    pixel.r = position.x;
                    pixel.g = position.y;
                    pixel.b = position.z;
                    pixel.a = 1;

                    SetError(Errors.PixelOutOfRange,
                        animationTextureErrors,
                        position.x > 1 || position.x < 0
                     || position.y > 1 || position.y < 0
                     || position.z > 1 || position.z < 0
                    );

                    clipColors[x + y * size] = pixel;

                    y++;
                    if (y == size)
                    {
                        x++;
                        y = 0;
                    }
                }

                DestroyImmediate(meshFrame);

                animator.Update(animationDeltaTime);

                yield return null;
            }
            result.SetPixels(clipColors, clip);
        }

        result.Apply();

        DestroyImmediate(prefab);

        result.filterMode = FilterMode.Point;
        AssetDatabase.ImportAsset(path);

        Debug.Log("Finished");
    }

    private IEnumerator CreateAnimationTextureIndividual(Vector2Int[] sizes, int vertexCount, int fps, Quaternion rotation, AnimationClip[] clips)
    {
        string extension = animationTextureColorMode == ColorMode.HDR ? "exr" : "png";
        string path = EditorUtility.SaveFilePanelInProject("Save Baked Animation Array", animationRigObject.name, extension, "Please save your baked animations");
        if (path.Length == 0)
        {
            yield break;
        }
        string filePrefix = path.Remove(path.Length - $".{extension}".Length);

        GameObject prefab = Instantiate(animationRigObject);
        Animator animator = prefab.GetComponentInChildren<Animator>();
        SkinnedMeshRenderer skinnedMesh = prefab.GetComponentInChildren<SkinnedMeshRenderer>();

        for (int clip = 0; clip < clips.Length; clip++)
        {
            Texture2D result = new Texture2D(sizes[clip].x, sizes[clip].y, DefaultFormat.HDR, TextureCreationFlags.None);
            float clipTime = clips[clip].length;
            int frames = (int)(clipTime * framesPerSecondCapture);

            Color[] clearColors = new Color[sizes[clip].x * sizes[clip].y];
            for (int i = 0; i < clearColors.Length; i++)
            {
                clearColors[i] = Color.clear;
            }
            result.SetPixels(clearColors);

            animator.Play(clips[clip].name, 0, 0);
            yield return null;
            animator.Update(0);

            float animationDeltaTime = 1f / fps;

            int y = 0;
            int x = 0;
            for (int i = 0; i < frames; i++)
            {
                Mesh meshFrame = new Mesh();
                skinnedMesh.BakeMesh(meshFrame);
                meshFrame.RecalculateBounds();

                //red = x
                //green = y
                //blue = z
                for (int j = 0; j < vertexCount; j++)
                {
                    Color pixel = Color.clear;
                    Vector3 position = rotation * meshFrame.vertices[j];
                    position = position * animationTextureScaler + Vector3.one * 0.5f;
                    pixel.r = position.x;
                    pixel.g = position.y;
                    pixel.b = position.z;
                    pixel.a = 1;

                    if (animationTextureColorMode == ColorMode.LDR)
                    {
                        SetError(Errors.PixelOutOfRange,
                            animationTextureErrors,
                            position.x > 1 || position.x < 0
                         || position.y > 1 || position.y < 0
                         || position.z > 1 || position.z < 0
                        );
                    }

                    result.SetPixel(x, y, pixel);

                    x++;
                    if (x == sizes[clip].x)
                    {
                        y++;
                        x = 0;
                    }
                }

                DestroyImmediate(meshFrame);

                animator.Update(animationDeltaTime);

                yield return null;
            }

            #region Export
            string clipPath = $"{filePrefix}@{clips[clip].name} f{frames}  s{sizes[clip]}.{extension}";
            byte[] encodedTex;
            if (animationTextureColorMode == ColorMode.HDR)
            {
                encodedTex = result.EncodeToEXR();
            }
            else
            {
                encodedTex = result.EncodeToPNG();
            }

            using (FileStream stream = File.Open(clipPath, FileMode.OpenOrCreate))
            {
                stream.Write(encodedTex, 0, encodedTex.Length);
            }

            AssetDatabase.ImportAsset(clipPath);
            DestroyImmediate(result);
            #endregion
        }

        DestroyImmediate(prefab);
        Debug.Log("Finished");
    }

    private IEnumerator CreateAnimationTexture(Vector2Int size, int vertexCount, int frames, int fps, Quaternion rotation, AnimationClip clip)
    {
        string extension = animationTextureColorMode == ColorMode.HDR ? "exr" : "png";
        string path = EditorUtility.SaveFilePanelInProject("Save Baked Animation Array", animationRigObject.name, animationTextureColorMode == ColorMode.HDR ? "exr" : "png", "Please save your baked animations");
        if (path.Length == 0)
        {
            yield break;
        }
        string filePrefix = path.Remove(path.Length - $".{extension}".Length);

        GameObject prefab = Instantiate(animationRigObject);
        Animator animator = prefab.GetComponentInChildren<Animator>();
        SkinnedMeshRenderer skinnedMesh = prefab.GetComponentInChildren<SkinnedMeshRenderer>();

        Texture2D result = new Texture2D(size.x, size.y, DefaultFormat.HDR, TextureCreationFlags.None);

        Color[] clearColors = new Color[size.x * size.y];
        for (int i = 0; i < clearColors.Length; i++)
        {
            clearColors[i] = Color.clear;
        }
        result.SetPixels(clearColors);

        animator.Play(clip.name, 0, 0);
        yield return null;
        animator.Update(0);

        float animationDeltaTime = 1f / fps;

        int y = 0;
        int x = 0;
        for (int i = 0; i < frames; i++)
        {
            if (i >= minFrame && i < maxFrame)
            {
                Mesh meshFrame = new Mesh();
                skinnedMesh.BakeMesh(meshFrame);
                meshFrame.RecalculateBounds();

                //red = x
                //green = y
                //blue = z
                for (int j = 0; j < vertexCount; j++)
                {
                    Color pixel = Color.clear;
                    Vector3 position = rotation * meshFrame.vertices[j];
                    position = position * animationTextureScaler + Vector3.one * 0.5f;
                    pixel.r = position.x;
                    pixel.g = position.y;
                    pixel.b = position.z;
                    pixel.a = 1;

                    SetError(Errors.PixelOutOfRange,
                        animationTextureErrors,
                        position.x > 1 || position.x < 0
                     || position.y > 1 || position.y < 0
                     || position.z > 1 || position.z < 0
                    );

                    result.SetPixel(x, y, pixel);

                    y++;
                    if (y == size.y)
                    {
                        x++;
                        y = 0;
                    }
                }

                DestroyImmediate(meshFrame);
            }

            animator.Update(animationDeltaTime);

            yield return null;
        }

        DestroyImmediate(prefab);

        #region Export
        string clipPath = $"{filePrefix}@{clip.name} f{frames} s{size}.{extension}";

        byte[] encodedTex;
        if (animationTextureColorMode == ColorMode.HDR)
        {
            encodedTex = result.EncodeToEXR();
        }
        else
        {
            encodedTex = result.EncodeToPNG();
        }

        using (FileStream stream = File.Open(clipPath, FileMode.OpenOrCreate))
        {
            stream.Write(encodedTex, 0, encodedTex.Length);
        }

        AssetDatabase.ImportAsset(clipPath);
        DestroyImmediate(result);
        #endregion

        Debug.Log("Finished");
    }

    public enum ColorMode
    {
        LDR,
        HDR
    }

    public enum BakeMode
    {
        Single,
        [InspectorName("All (Individual)")] AllIndividual,
        [InspectorName("All (Texture2DArray)")] AllTexture2DArray,
    }
    #endregion

    #region Mesh Texture
    private void MeshTextureEditor()
    {
        textureMesh = (Mesh)EditorGUILayout.ObjectField("Mesh", textureMesh, typeof(Mesh), false);

        if (SetError(Errors.MissingMesh, meshTextureErrors, textureMesh == null))
        {
            return;
        }

        meshTextureScaler = EditorGUILayout.FloatField("Scaler", meshTextureScaler);
        meshTextureColorMode = (ColorMode)EditorGUILayout.EnumPopup("Color Mode", meshTextureColorMode);

        int squareFrames = Mathf.FloorToInt(Mathf.Sqrt(textureMesh.vertexCount));
        int textureSize = Mathf.NextPowerOfTwo(squareFrames);

        EditorGUILayout.Space(EditorGUIUtility.singleLineHeight);
        EditorGUILayout.LabelField($"Pixels to fill: {textureMesh.vertexCount}");
        EditorGUILayout.LabelField($"Result texture size: {textureSize}");
        if (GUILayout.Button($"Bake Mesh to Texture"))
        {
            BakeMeshToTexture(textureMesh, meshTextureScaler, textureSize, meshTextureColorMode);
        }
    }

    private void BakeMeshToTexture(Mesh mesh, float scaler, int size, ColorMode colorMode)
    {
        string extension = colorMode == ColorMode.HDR ? "exr" : "png";
        string path = EditorUtility.SaveFilePanelInProject("Save Mesh Texture", mesh.name + " Baked", extension, "Please save your baked mesh texture");
        if (path.Length == 0)
        {
            return;
        }
        string filePrefix = path.Remove(path.Length - $".{extension}".Length);

        mesh.RecalculateBounds();

        Texture2D result = new Texture2D(size, size, DefaultFormat.HDR, TextureCreationFlags.None);

        int vertexCount = mesh.vertexCount;
        int x = 0;
        int y = 0;
        for (int j = 0; j < vertexCount; j++)
        {
            Color pixel = Color.clear;
            Vector3 position = mesh.vertices[j] * animationTextureScaler + Vector3.one * 0.5f;
            pixel.r = position.x * scaler;
            pixel.g = position.y * scaler;
            pixel.b = position.z * scaler;
            pixel.a = 1;

            SetError(Errors.PixelOutOfRange,
                meshTextureErrors,
                position.x > 1 || position.x < 0
             || position.y > 1 || position.y < 0
             || position.z > 1 || position.z < 0
            );

            result.SetPixel(x, y, pixel);

            y++;
            if (y == size)
            {
                x++;
                y = 0;
            }
        }

        #region Export
        string clipPath = $"{filePrefix} v{vertexCount} s{size}.{extension}";
        byte[] encodedTex;
        if (colorMode == ColorMode.HDR)
        {
            encodedTex = result.EncodeToEXR();
        }
        else
        {
            encodedTex = result.EncodeToPNG();
        }

        using (FileStream stream = File.Open(clipPath, FileMode.OpenOrCreate))
        {
            stream.Write(encodedTex, 0, encodedTex.Length);
        }

        AssetDatabase.ImportAsset(clipPath);
        DestroyImmediate(result);
        #endregion
    }
    #endregion

    #region UV Map
    private void UVMapEditor()
    {
        uvMesh = (Mesh)EditorGUILayout.ObjectField("Mesh", uvMesh, typeof(Mesh), true);

        if (SetError(Errors.MissingUVMesh, uvErrors, uvMesh == null))
        {
            return;
        }

        uvLayer = (UVLayer)EditorGUILayout.EnumPopup("UV Layer", uvLayer);
        uvMeshScale = EditorGUILayout.FloatField("Mesh Scale", uvMeshScale);

        List<Vector2> uvList = new List<Vector2>();
        uvMesh.GetUVs((int)uvLayer, uvList);

        if (uvList.Count > 0)
        {
            GUILayout.Label(Errors.UVAlreadyExists);
        }

        EditorGUILayout.Space(EditorGUIUtility.singleLineHeight);
        if (GUILayout.Button($"Apply UVs to {uvLayer}"))
        {
            ApplyUVToLayer();
        }
    }

    private void ApplyUVToLayer()
    {
        string path = EditorUtility.SaveFilePanelInProject("Save UV Mesh", uvMesh.name + " UV", "asset", "Please save your UV mesh");
        if (path.Length == 0)
        {
            return;
        }

        Mesh mesh = new Mesh();
        Vector3[] vertices = uvMesh.vertices;
        for (int i = 0; i < uvMesh.vertices.Length; i++)
        {
            vertices[i] *= uvMeshScale;
        }
        mesh.SetVertices(vertices);
        mesh.SetIndices(uvMesh.GetIndices(0), MeshTopology.Triangles, 0);
        mesh.uv = uvMesh.uv;
        mesh.normals = uvMesh.normals;
        mesh.RecalculateBounds();

        Vector2[] resultUV = new Vector2[mesh.vertexCount];
        for (int i = 0; i < resultUV.Length; i++)
        {
            resultUV[i].x = i;
        }

        mesh.SetUVs((int)uvLayer, resultUV);
        AssetDatabase.CreateAsset(mesh, path);
    }

    public enum UVLayer
    {
        UV0,
        UV1,
        UV2,
        UV3,
        UV4,
        UV5,
        UV6,
        UV7,
    }
    #endregion

    #region Error Utility
    private bool SetError(string error, List<string> errorSet, bool condition)
    {
        if (condition && !errorSet.Contains(error))
        {
            errorSet.Add(error);
        }
        else if (!condition && errorSet.Contains(error))
        {
            errorSet.Remove(error);
        }

        return condition;
    }

    private void ErrorView(List<string> errors)
    {
        foreach (string error in errors)
        {
            GUILayout.Label(error);
        }
    }

    private class Errors
    {
        public const string ErrorPrefix = "ERROR: ";
        public const string WarningPrefix = "Warning: ";

        public const string MissingRigObject = ErrorPrefix + "An animation rig object is not assinged for texture creation.  Please assign one.";
        public const string MissingSkinnedMeshRenderer = ErrorPrefix + "Could not find a Skinned Mesh Renderer in the object's hierarchy.";
        public const string MissingAnimator = ErrorPrefix + "Could not find an Animator in the object's hierarchy.";
        public const string MissingRuntimeAnimatorController = ErrorPrefix + "Could not find a Runtime Animator Controller in the Animator's properties.";
        public const string MissingUVMesh = ErrorPrefix + "A mesh is not assigned for UV application.  Please assign one.";
        public const string MissingMesh = ErrorPrefix + "A mesh is not assigned for baking.  Please assign one.";

        public const string NoAnimationClips = WarningPrefix + "There are no animation clips on this animator.  You can't bake nonexistant clips.";
        public const string UVAlreadyExists = WarningPrefix + "This mesh already has assigned UVs on this layer.  Applying will overwrite them.";
        public const string PixelOutOfRange = WarningPrefix + "A pixel's value was out of range (less than 0 or greater than 1).  The texture will save with the clamped pixel if set to LDR.";
    }
    #endregion
}
#endif
