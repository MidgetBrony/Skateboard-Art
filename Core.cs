using HarmonyLib;
using MelonLoader;
using ModsPanel;
using SteamShelf;
using SteamShelf.Placeables;
using SteamShelf.Save;
using SteamShelf.UI;
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEngine;

[assembly: MelonInfo(typeof(BR_SkateboardArt.Core), "Skateboard Art", "0.2.3", "Rusty")]
[assembly: MelonGame("NestedLoop", "BOXROOM")]

namespace BR_SkateboardArt;

public sealed class Core : MelonMod
{
    internal static MelonLogger.Instance Log { get; private set; }

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        Directory.CreateDirectory(ArtStore.DirectoryPath);
        HarmonyInstance.PatchAll();
        LoggerInstance.Msg("Skateboard Art active. Use Alternate Interact on a wall skateboard to edit its artwork.");
    }
}

[HarmonyPatch(typeof(PlaceableManager), nameof(PlaceableManager.InstantiatePlaceableAsync))]
internal static class SkateboardSpawnPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlaceableData data, ref Task<GameObject> __result)
    {
        if (data != null && SkateboardArtController.IsSkateboardId(data.ID))
        {
            __result = AttachWhenReady(__result);
        }
    }

    private static async Task<GameObject> AttachWhenReady(Task<GameObject> original)
    {
        GameObject instance = await original;
        if (instance != null && instance.GetComponent<SkateboardArtController>() == null)
        {
            SkateboardArtController controller = instance.AddComponent<SkateboardArtController>();
            if (!controller.IsReady)
            {
                UnityEngine.Object.Destroy(controller);
                Core.Log.Warning($"Could not find the deck-art material on '{instance.name}'.");
            }
            else
            {
                controller.InstallInteractionTargets();
            }
        }
        return instance;
    }
}

[HarmonyPatch(typeof(GameImagePainter), nameof(GameImagePainter.SetCustomImage))]
internal static class SkateboardImageInteractionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(GameImagePainter __instance)
    {
        SkateboardArtController controller = __instance.GetComponentInParent<SkateboardArtController>();
        if (controller == null)
        {
            return true;
        }

        controller.OpenEditor(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(GameImagePainter), nameof(GameImagePainter.SetCustomImagePath))]
internal static class SkateboardNativeImagePatch
{
    [HarmonyPostfix]
    private static void Postfix(GameImagePainter __instance, string path)
    {
        SkateboardArtController controller = __instance.GetComponentInParent<SkateboardArtController>();
        controller?.AcceptNativeImage(path);
    }
}

[HarmonyPatch(typeof(GameImagePainter), nameof(GameImagePainter.SaveState), MethodType.Getter)]
internal static class SkateboardNativeSavePatch
{
    [HarmonyPostfix]
    private static void Postfix(GameImagePainter __instance, ref PlaceableSaveState __result)
    {
        SkateboardArtController controller = __instance.GetComponentInParent<SkateboardArtController>();
        if (controller != null && controller.IsInteractionPainter(__instance))
        {
            controller.MergeTransformIntoNativeSave(__result);
        }
    }
}

[HarmonyPatch(typeof(GameImagePainter), nameof(GameImagePainter.PopulateFromLoad))]
internal static class SkateboardNativeLoadPatch
{
    [HarmonyPostfix]
    private static void Postfix(GameImagePainter __instance, PlaceableSaveState saveState)
    {
        SkateboardArtController controller = __instance.GetComponentInParent<SkateboardArtController>();
        if (controller != null && controller.IsInteractionPainter(__instance))
        {
            controller.RestoreTransformFromNativeSave(saveState);
        }
    }
}

[HarmonyPatch(typeof(Menu_CustomImage), "OnPathChanged")]
internal static class SkateboardImageMenuSuccessPatch
{
    [HarmonyPostfix]
    private static void Postfix(Menu_CustomImage __instance)
    {
        SkateboardArtController.ReopenAfterNativeMenu(__instance);
    }
}

[HarmonyPatch(typeof(Menu_CustomImage), "OnPathFailed")]
internal static class SkateboardImageMenuCancelPatch
{
    [HarmonyPostfix]
    private static void Postfix(Menu_CustomImage __instance)
    {
        SkateboardArtController.ReopenAfterNativeMenu(__instance);
    }
}

[Serializable]
internal sealed class SkateboardArtSaveData
{
    public int appId;
    public int screenshotIndex;
    public string customUrl = "";
    public string albumId = "";
    public string marker = "BR_SkateboardArt_v1";
    public string imagePath = "";
    public float offsetX;
    public float offsetY;
    public float zoom = 1f;

    public SkateboardArtSaveData Clone()
    {
        return new SkateboardArtSaveData
        {
            appId = appId,
            screenshotIndex = screenshotIndex,
            customUrl = customUrl,
            albumId = albumId,
            marker = marker,
            imagePath = imagePath,
            offsetX = offsetX,
            offsetY = offsetY,
            zoom = zoom
        };
    }
}

public sealed class SkateboardArtController : MonoBehaviour, IPlaceable
{
    private const float MinZoom = 1f;
    private const float MaxZoom = 6f;
    private static readonly FieldInfo PainterRenderer = AccessTools.Field(typeof(GameImagePainter), "targetRenderer");
    private static readonly FieldInfo PainterMaterialIndex = AccessTools.Field(typeof(GameImagePainter), "materialIndex");
    private static readonly FieldInfo PainterScreenshots = AccessTools.Field(typeof(GameImagePainter), "shouldPaintScreenshots");
    private static readonly FieldInfo PainterProvider = AccessTools.Field(typeof(GameImagePainter), "provider");
    private static readonly FieldInfo CustomImageMenuPainter = AccessTools.Field(typeof(Menu_CustomImage), "imagePainter");
    private static SkateboardArtController activeEditor;

    private Renderer targetRenderer;
    private Material targetMaterial;
    private int materialIndex = -1;
    private Texture originalTexture;
    private Vector2 originalScale;
    private Vector2 originalOffset;
    private readonly MaterialPropertyBlock propertyBlock = new();
    private Texture2D loadedTexture;
    private string loadedTexturePath = "";
    private GameImagePainter interactionPainter;
    private PlaceableSaveState saveState;
    private SkateboardArtSaveData committed = new();
    private SkateboardArtSaveData working = new();
    private ModMenu editorMenu;
    private bool editorSession;
    private bool awaitingNativePicker;

    public bool IsReady => targetMaterial != null;

    public PlaceableSaveState SaveState
    {
        get
        {
            EnsureSaveState();
            saveState.CustomData = JsonUtility.ToJson(committed);
            return saveState;
        }
    }

    public static bool IsSkateboardId(string id)
    {
        return !string.IsNullOrEmpty(id) &&
               (id.Equals("Skateboard_01", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("Skateboard_02", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("Skaterboard_03", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("Skateboard_03", StringComparison.OrdinalIgnoreCase));
    }

    private void Awake()
    {
        LocateArtSurface();
    }

    private void LocateArtSurface()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            Material[] materials = renderer.materials;
            for (int index = 0; index < materials.Length; index++)
            {
                Material material = materials[index];
                string materialName = material != null ? material.name : "";
                if (materialName.IndexOf("Skateboard_Deck", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                BindArtSurface(renderer, index);
                return;
            }
        }
    }

    private void BindArtSurface(Renderer renderer, int index)
    {
        if (renderer == null || index < 0 || index >= renderer.sharedMaterials.Length)
        {
            return;
        }

        Material[] materials = renderer.materials;
        Material material = materials[index];
        if (material == null)
        {
            return;
        }

        targetRenderer = renderer;
        targetMaterial = material;
        materialIndex = index;
        originalTexture = material.mainTexture;
        originalScale = GetTextureScale(material);
        originalOffset = GetTextureOffset(material);
    }

    internal void InstallInteractionTargets()
    {
        if (!IsReady)
        {
            return;
        }

        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        int installed = 0;
        foreach (Collider collider in colliders)
        {
            if (collider == null || collider.isTrigger)
            {
                continue;
            }

            GameImagePainter existingPainter = collider.GetComponent<GameImagePainter>();
            if (existingPainter != null)
            {
                Renderer nativeRenderer = PainterRenderer.GetValue(existingPainter) as Renderer;
                object nativeIndexValue = PainterMaterialIndex.GetValue(existingPainter);
                int nativeIndex = nativeIndexValue is int value ? value : -1;
                if (nativeRenderer != null && nativeIndex >= 0)
                {
                    BindArtSurface(nativeRenderer, nativeIndex);
                }
                interactionPainter ??= existingPainter;
                installed++;
                continue;
            }

            GameObject host = collider.gameObject;
            bool wasActive = host.activeSelf;
            if (wasActive)
            {
                host.SetActive(false);
            }

            SteamGameDataProvider provider = host.GetComponent<SteamGameDataProvider>() ?? host.AddComponent<SteamGameDataProvider>();
            GameImagePainter painter = host.AddComponent<GameImagePainter>();
            PainterRenderer.SetValue(painter, targetRenderer);
            PainterMaterialIndex.SetValue(painter, materialIndex);
            PainterScreenshots.SetValue(painter, false);
            PainterProvider.SetValue(painter, provider);
            interactionPainter ??= painter;

            if (wasActive)
            {
                host.SetActive(true);
            }
            installed++;
        }

        if (installed == 0)
        {
            Core.Log.Warning($"No usable interaction collider was found on '{name}'.");
        }
        else
        {
            Core.Log.Msg($"Bound '{name}' to native painter material {materialIndex} on '{targetRenderer?.name}'.");
        }
    }

    public void OpenEditor(GameImagePainter sourcePainter = null)
    {
        if (!IsReady)
        {
            return;
        }

        if (sourcePainter != null &&
            sourcePainter.GetComponentInParent<SkateboardArtController>() == this)
        {
            interactionPainter = sourcePainter;
        }

        if (activeEditor != null && activeEditor != this)
        {
            activeEditor.CancelEditor();
        }

        activeEditor = this;
        if (!editorSession)
        {
            working = committed.Clone();
            Apply(working);
            editorSession = true;
        }
        ShowEditorMenu();
    }

    private void ShowEditorMenu()
    {
        string fileLabel = string.IsNullOrEmpty(working.imagePath) ? "Stock artwork" : Path.GetFileName(working.imagePath);
        editorMenu = ModsUi.CreateMenu("Rusty.BR-SkateboardArt.Editor", "Skateboard Art", fileLabel);
        editorMenu.Eyebrow = "LIVE ART EDITOR";
        editorMenu.CloseText = "CANCEL";
        editorMenu.CompactWindow = true;
        editorMenu.Draggable = true;
        editorMenu.Closed = OnEditorMenuClosed;
        editorMenu
            .AddButton("Change Art...", BeginNativeImagePicker, "BOXROOM CUSTOM IMAGE BROWSER")
            .AddSlider("Move X", () => working.offsetX, value => SetOffsetX(value), -1f, 1f, false, value => value.ToString("0.00"))
            .AddSlider("Move Y", () => working.offsetY, value => SetOffsetY(value), -1f, 1f, false, value => value.ToString("0.00"))
            .AddSlider("Zoom", () => working.zoom, value => SetZoom(value), MinZoom, MaxZoom, false, value => value.ToString("0.00") + "x")
            .AddButton("Reset Position", ResetPosition)
            .AddButton("Use Stock Art", UseStockArt)
            .AddButton("Reload Saved", ReloadCommitted)
            .AddButton("Save & Close", SaveAndClose, "SAVED WITH THIS SKATEBOARD");
        editorMenu.Show();
    }

    private void SetOffsetX(float value)
    {
        working.offsetX = value;
        Apply(working);
    }

    private void SetOffsetY(float value)
    {
        working.offsetY = value;
        Apply(working);
    }

    private void SetZoom(float value)
    {
        working.zoom = value;
        Apply(working);
    }

    private void ResetPosition()
    {
        working.offsetX = 0f;
        working.offsetY = 0f;
        working.zoom = 1f;
        Apply(working);
    }

    private void UseStockArt()
    {
        working.imagePath = "";
        ResetPosition();
    }

    private void ReloadCommitted()
    {
        working = committed.Clone();
        Apply(working);
    }

    private void BeginNativeImagePicker()
    {
        if (interactionPainter == null)
        {
            GameImagePainter[] painters = GetComponentsInChildren<GameImagePainter>(true);
            foreach (GameImagePainter painter in painters)
            {
                if (painter != null &&
                    painter.GetComponentInParent<SkateboardArtController>() == this)
                {
                    interactionPainter = painter;
                    break;
                }
            }
        }

        if (interactionPainter == null)
        {
            ModsUi.ShowToast("The skateboard image target is unavailable.", 5f);
            return;
        }

        awaitingNativePicker = true;
        editorMenu?.Close();
        Singleton<MenuManager>.Instance.OpenMenu("CustomImage", interactionPainter);
    }

    internal void AcceptNativeImage(string selectedPath)
    {
        if (!editorSession || !awaitingNativePicker || string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        try
        {
            working.imagePath = ArtStore.Import(selectedPath);
            working.offsetX = 0f;
            working.offsetY = 0f;
            working.zoom = 1f;
            Apply(working);
        }
        catch (Exception ex)
        {
            Core.Log.Error($"Could not import skateboard artwork: {ex.Message}");
            ModsUi.ShowToast("Could not import that image. Check Latest.log.", 6f);
        }
    }

    internal bool IsInteractionPainter(GameImagePainter painter)
    {
        return painter != null && painter == interactionPainter;
    }

    internal void MergeTransformIntoNativeSave(PlaceableSaveState nativeSaveState)
    {
        if (nativeSaveState == null)
        {
            return;
        }

        PainterSaveData native = null;
        if (!string.IsNullOrEmpty(nativeSaveState.CustomData))
        {
            try { native = JsonUtility.FromJson<PainterSaveData>(nativeSaveState.CustomData); }
            catch { native = null; }
        }
        native ??= new PainterSaveData();

        SkateboardArtSaveData combined = committed.Clone();
        combined.marker = "BR_SkateboardArt_v1";
        combined.appId = native.appId;
        combined.screenshotIndex = native.screenshotIndex;
        combined.albumId = native.albumId ?? "";
        combined.customUrl = native.customUrl ?? "";

        if (!string.IsNullOrEmpty(combined.customUrl) && !string.IsNullOrEmpty(committed.imagePath))
        {
            combined.customUrl = committed.imagePath;
            combined.imagePath = committed.imagePath;
        }
        else if (string.IsNullOrEmpty(combined.customUrl))
        {
            combined.imagePath = "";
        }

        nativeSaveState.CustomData = JsonUtility.ToJson(combined);
    }

    internal void RestoreTransformFromNativeSave(PlaceableSaveState nativeSaveState)
    {
        if (nativeSaveState == null || string.IsNullOrEmpty(nativeSaveState.CustomData))
        {
            return;
        }

        try
        {
            bool hasExtendedData = nativeSaveState.CustomData.IndexOf(
                "\"marker\":\"BR_SkateboardArt_v1\"", StringComparison.Ordinal) >= 0;
            SkateboardArtSaveData parsed = JsonUtility.FromJson<SkateboardArtSaveData>(nativeSaveState.CustomData);
            if (parsed == null)
            {
                return;
            }

            if (!hasExtendedData)
            {
                // Upgrade a pre-0.2.2 native painter save without discarding its art.
                parsed = new SkateboardArtSaveData
                {
                    appId = parsed.appId,
                    screenshotIndex = parsed.screenshotIndex,
                    customUrl = parsed.customUrl ?? "",
                    albumId = parsed.albumId ?? "",
                    imagePath = parsed.customUrl ?? "",
                    offsetX = 0f,
                    offsetY = 0f,
                    zoom = 1f
                };
            }

            parsed.zoom = Mathf.Clamp(parsed.zoom, MinZoom, MaxZoom);
            parsed.offsetX = Mathf.Clamp(parsed.offsetX, -1f, 1f);
            parsed.offsetY = Mathf.Clamp(parsed.offsetY, -1f, 1f);
            committed = parsed;
            working = committed.Clone();
            saveState = nativeSaveState;

            if (!string.IsNullOrEmpty(committed.imagePath))
            {
                Apply(committed);
            }

            Core.Log.Msg($"Restored skateboard transform: X={committed.offsetX:0.00}, Y={committed.offsetY:0.00}, Zoom={committed.zoom:0.00}.");
        }
        catch (Exception ex)
        {
            Core.Log.Warning($"Could not restore skateboard transform data: {ex.Message}");
        }
    }

    internal static void ReopenAfterNativeMenu(Menu_CustomImage nativeMenu)
    {
        GameImagePainter painter = CustomImageMenuPainter?.GetValue(nativeMenu) as GameImagePainter;
        SkateboardArtController controller = painter != null
            ? painter.GetComponentInParent<SkateboardArtController>()
            : activeEditor;
        if (controller != null && controller.awaitingNativePicker)
        {
            MelonCoroutines.Start(controller.ReopenAfterNativeMenuRoutine());
        }
    }

    private IEnumerator ReopenAfterNativeMenuRoutine()
    {
        yield return null;
        awaitingNativePicker = false;
        if (editorSession)
        {
            ShowEditorMenu();
        }
    }

    private void SaveAndClose()
    {
        committed = working.Clone();
        EnsureSaveState();
        saveState.CustomData = JsonUtility.ToJson(committed);
        editorSession = false;
        editorMenu?.Close();
        if (activeEditor == this) activeEditor = null;
        ModsUi.ShowToast("Skateboard artwork saved.", 3f);
    }

    private void CancelEditor()
    {
        if (!editorSession) return;
        working = committed.Clone();
        Apply(committed);
        editorSession = false;
        awaitingNativePicker = false;
        editorMenu?.Close();
        if (activeEditor == this) activeEditor = null;
    }

    private void OnEditorMenuClosed()
    {
        if (awaitingNativePicker || !editorSession) return;
        working = committed.Clone();
        Apply(committed);
        editorSession = false;
        if (activeEditor == this) activeEditor = null;
    }

    private void Apply(SkateboardArtSaveData data)
    {
        if (!IsReady || data == null)
        {
            return;
        }

        Texture texture = originalTexture;
        if (!string.IsNullOrEmpty(data.imagePath))
        {
            if (loadedTexture != null && string.Equals(loadedTexturePath, data.imagePath, StringComparison.OrdinalIgnoreCase))
            {
                texture = loadedTexture;
            }
            else
            {
                Texture2D replacement = LoadTexture(data.imagePath);
                if (loadedTexture != null)
                {
                    UnityEngine.Object.Destroy(loadedTexture);
                }
                loadedTexture = replacement;
                loadedTexturePath = replacement != null ? data.imagePath : "";
                if (replacement != null)
                {
                    texture = replacement;
                }
            }
        }
        else if (loadedTexture != null)
        {
            UnityEngine.Object.Destroy(loadedTexture);
            loadedTexture = null;
            loadedTexturePath = "";
        }

        float zoom = Mathf.Clamp(data.zoom, MinZoom, MaxZoom);
        Vector2 scale = originalScale / zoom;
        Vector2 spare = originalScale - scale;
        Vector2 minimumPan = originalScale * 0.2f;
        Vector2 panRange = new(Mathf.Max(Mathf.Abs(spare.x), Mathf.Abs(minimumPan.x)),
            Mathf.Max(Mathf.Abs(spare.y), Mathf.Abs(minimumPan.y)));
        Vector2 offset = originalOffset + spare * 0.5f +
            new Vector2(data.offsetX * panRange.x * 0.5f, data.offsetY * panRange.y * 0.5f);

        Material[] materials = targetRenderer.materials;
        if (materialIndex < 0 || materialIndex >= materials.Length || materials[materialIndex] == null)
        {
            Core.Log.Warning($"Skateboard art material index {materialIndex} is no longer valid on '{targetRenderer.name}'.");
            return;
        }

        targetMaterial = materials[materialIndex];
        SetTexture(targetMaterial, texture);
        SetTextureTransform(targetMaterial, scale, offset);
        materials[materialIndex] = targetMaterial;
        targetRenderer.materials = materials;

        targetRenderer.GetPropertyBlock(propertyBlock, materialIndex);
        propertyBlock.SetTexture("_BaseMap", texture);
        propertyBlock.SetTexture("_MainTex", texture);
        propertyBlock.SetVector("_BaseMap_ST", new Vector4(scale.x, scale.y, offset.x, offset.y));
        propertyBlock.SetVector("_MainTex_ST", new Vector4(scale.x, scale.y, offset.x, offset.y));
        targetRenderer.SetPropertyBlock(propertyBlock, materialIndex);
    }

    private static Texture2D LoadTexture(string path)
    {
        if (!File.Exists(path))
        {
            Core.Log.Warning($"Saved skateboard artwork is missing: {path}");
            return null;
        }

        byte[] bytes = File.ReadAllBytes(path);
        Texture2D texture = new(2, 2, TextureFormat.RGBA32, true);
        if (!texture.LoadImage(bytes))
        {
            UnityEngine.Object.Destroy(texture);
            Core.Log.Warning($"Saved skateboard artwork could not be decoded: {path}");
            return null;
        }
        texture.name = "BR_SkateboardArt_" + Path.GetFileNameWithoutExtension(path);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        return texture;
    }

    private static Vector2 GetTextureScale(Material material)
    {
        if (material.HasProperty("_BaseMap")) return material.GetTextureScale("_BaseMap");
        return material.mainTextureScale;
    }

    private static Vector2 GetTextureOffset(Material material)
    {
        if (material.HasProperty("_BaseMap")) return material.GetTextureOffset("_BaseMap");
        return material.mainTextureOffset;
    }

    private static void SetTexture(Material material, Texture texture)
    {
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        material.mainTexture = texture;
    }

    private static void SetTextureTransform(Material material, Vector2 scale, Vector2 offset)
    {
        if (material.HasProperty("_BaseMap"))
        {
            material.SetTextureScale("_BaseMap", scale);
            material.SetTextureOffset("_BaseMap", offset);
        }
        if (material.HasProperty("_MainTex"))
        {
            material.SetTextureScale("_MainTex", scale);
            material.SetTextureOffset("_MainTex", offset);
        }
        material.mainTextureScale = scale;
        material.mainTextureOffset = offset;
    }

    private void EnsureSaveState()
    {
        saveState ??= new PlaceableSaveState(name, transform.position, transform.rotation, transform.localScale);
    }

    public void PopulateFromLoad(PlaceableSaveState loadedState)
    {
        saveState = loadedState;
        if (loadedState == null || string.IsNullOrEmpty(loadedState.CustomData))
        {
            committed = new SkateboardArtSaveData();
            working = committed.Clone();
            Apply(committed);
            return;
        }

        try
        {
            SkateboardArtSaveData parsed = JsonUtility.FromJson<SkateboardArtSaveData>(loadedState.CustomData);
            if (parsed != null && parsed.marker == "BR_SkateboardArt_v1")
            {
                parsed.zoom = Mathf.Clamp(parsed.zoom, MinZoom, MaxZoom);
                parsed.offsetX = Mathf.Clamp(parsed.offsetX, -1f, 1f);
                parsed.offsetY = Mathf.Clamp(parsed.offsetY, -1f, 1f);
                committed = parsed;
                working = committed.Clone();
                Apply(committed);
            }
        }
        catch (Exception ex)
        {
            Core.Log.Warning($"Could not load skateboard artwork state: {ex.Message}");
        }
    }

    public void LinkSaveState(PlaceableSaveState linkedState)
    {
        saveState = linkedState;
        if (saveState != null)
        {
            saveState.CustomData = JsonUtility.ToJson(committed);
        }
    }

    public void RefreshSaveStateReference(PlaceableSaveState newState)
    {
        saveState = newState;
    }

    public void OnPlaced() { }
    public void OnPickedUp() { }

    public void OnDeleted()
    {
        CancelEditor();
    }

    private void OnDestroy()
    {
        CancelEditor();
        if (loadedTexture != null)
        {
            UnityEngine.Object.Destroy(loadedTexture);
            loadedTexture = null;
            loadedTexturePath = "";
        }
    }
}

internal static class ArtStore
{
    internal static readonly string DirectoryPath = Path.GetFullPath(
        Path.Combine(Application.dataPath, "..", "UserData", "SkateboardArt", "Images"));

    internal static string Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Selected image does not exist.", sourcePath);
        }

        FileInfo info = new(sourcePath);
        if (info.Length > 64L * 1024L * 1024L)
        {
            throw new InvalidDataException("Images larger than 64 MB are not supported.");
        }

        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
        {
            throw new InvalidDataException("Choose a PNG or JPEG image.");
        }

        Directory.CreateDirectory(DirectoryPath);
        byte[] bytes = File.ReadAllBytes(sourcePath);
        string hash;
        using (SHA256 sha = SHA256.Create())
        {
            hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        string destination = Path.Combine(DirectoryPath, hash + extension);
        if (!File.Exists(destination))
        {
            File.WriteAllBytes(destination, bytes);
        }
        return destination;
    }
}
