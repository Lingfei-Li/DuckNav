using Duckov.MiniMaps;
using Duckov.Modding;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace DuckNav
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // UI Containers
        private Canvas _overlayCanvas;
        private RectTransform _radarContainer; // Center screen for arrows
        private Font _uiFont;
        private Transform _lastHud;

        // Logic
        private const float UpdateInterval = 0.02f; // Fast loop for smooth animation
        private const float ScanInterval = 1.0f;    // Slow loop for finding new objects
        private float _lastScanTime = 0f;

        // Tracking Markers
        // We map the POI object -> The Radar Arrow GameObject
        private Dictionary<object, GameObject> _radarArrows = new Dictionary<object, GameObject>();

        // Cache the list of found markers so we don't scan every frame
        private HashSet<MapMarkerPOI> _cachedSceneMarkers = new HashSet<MapMarkerPOI>();

        private void Start()
        {
            Debug.Log("[DuckNav] Start");
            this.StartCoroutine(this.Boot());
        }

        private IEnumerator Boot()
        {
            ModBehaviour modBehaviour = this;

            // 1. Wait for Player
            while (CharacterMainControl.Main == null)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            // 2. Wait for HUD
            while (true)
            {
                Transform hudCanvas = modBehaviour.FindHudCanvas();
                if (hudCanvas != null)
                {
                    modBehaviour._lastHud = hudCanvas;
                    modBehaviour.AdoptHudFont(hudCanvas);
                    modBehaviour.SetupOverlay(hudCanvas);
                    modBehaviour.StartCoroutine(modBehaviour.Loop());
                    yield break;
                }
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        private IEnumerator Loop()
        {
            ModBehaviour modBehaviour = this;
            WaitForSecondsRealtime intervalWait = new WaitForSecondsRealtime(ModBehaviour.UpdateInterval);
            WaitForSecondsRealtime errorWait = new WaitForSecondsRealtime(1f);

            while (true)
            {
                bool errorOccurred = false;
                try
                {
                    // Re-init check if HUD is lost (e.g. scene change)
                    if (modBehaviour._lastHud == null || !modBehaviour._lastHud.gameObject.activeInHierarchy)
                    {
                        Transform hudCanvas = modBehaviour.FindHudCanvas();
                        if (hudCanvas != null)
                        {
                            modBehaviour._lastHud = hudCanvas;
                            modBehaviour.AdoptHudFont(hudCanvas);
                            modBehaviour.SetupOverlay(hudCanvas);
                        }
                    }

                    if (CharacterMainControl.Main != null && modBehaviour._radarContainer != null)
                    {
                        modBehaviour.ProcessMarkers();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[DuckNav] Error in loop: " + ex.ToString());
                    errorOccurred = true;
                }

                yield return errorOccurred ? errorWait : intervalWait;
            }
        }

        private void ScanForMarkers()
        {
            // Only run this heavy scan once every second
            if (Time.unscaledTime - _lastScanTime < ScanInterval) return;

            _lastScanTime = Time.unscaledTime;

            // Clear and rebuild the cache of scene markers
            _cachedSceneMarkers.Clear();

            // This forces Unity to find all active MapMarkerPOI components in the scene,
            // bypassing the need for the Minimap to register them first.
            MapMarkerPOI[] found = UnityEngine.Object.FindObjectsOfType<MapMarkerPOI>();
            if (found != null)
            {
                foreach (var m in found) _cachedSceneMarkers.Add(m);
            }
        }

        private void ProcessMarkers()
        {
            Transform playerT = CharacterMainControl.Main.transform;
            Vector3 playerPos = playerT.position;
            Vector3 playerFwd = playerT.forward;

            // 1. Run the slow scan to find new objects in the scene
            ScanForMarkers();

            // 2. Aggregate all markers (from Scene Scan + Static List as backup)
            HashSet<object> currentFramePOIs = new HashSet<object>();

            // Add cached scene markers
            foreach (var m in _cachedSceneMarkers)
            {
                if (m != null) currentFramePOIs.Add(m);
            }

            // Add standard list markers (just in case there are non-MonoBehaviour ones or duplicates)
            var staticPoints = PointsOfInterests.Points;
            if (staticPoints != null)
            {
                foreach (var poi in staticPoints)
                {
                    if (poi is MapMarkerPOI mapMarker)
                    {
                        currentFramePOIs.Add(mapMarker);
                    }
                }
            }

            // 3. Update UI
            foreach (var obj in currentFramePOIs)
            {
                MapMarkerPOI mapMarker = obj as MapMarkerPOI;
                if (mapMarker == null) continue;

                // Ensure Arrow Exists
                if (!_radarArrows.ContainsKey(mapMarker))
                {
                    GameObject arrow = CreateRadarArrow(mapMarker);
                    _radarArrows.Add(mapMarker, arrow);
                }

                // Update Arrow Position
                UpdateRadarVisuals(_radarArrows[mapMarker], mapMarker, playerPos, playerFwd);
            }

            // 4. Cleanup Stale Markers (markers removed from game/scene)

            // Cleanup Arrows
            var arrowKeys = _radarArrows.Keys.ToList();
            foreach (var key in arrowKeys)
            {
                if (!currentFramePOIs.Contains(key))
                {
                    UnityEngine.Object.Destroy(_radarArrows[key]);
                    _radarArrows.Remove(key);
                }
            }
        }

        private void UpdateRadarVisuals(GameObject arrowObj, MapMarkerPOI poi, Vector3 playerPos, Vector3 playerFwd)
        {
            Vector3 targetPos = poi.Data.worldPosition;
            Vector3 dirToTarget = targetPos - playerPos;
            dirToTarget.y = 0;
            playerFwd.y = 0;

            float distance = Vector3.Distance(playerPos, targetPos);
            float angle = Vector3.SignedAngle(playerFwd, dirToTarget, Vector3.up);

            // Update Distance Text
            Text t = arrowObj.GetComponentInChildren<Text>();
            if (t != null) t.text = $"{distance:F0}m";

            // Update Icon Color
            Image iconImg = arrowObj.GetComponent<Image>();
            if (iconImg != null) iconImg.color = poi.Color;

            // --- ADJUSTED SCALING LOGIC ---

            // 1. Calculate tVal (0.0 to 1.0)
            // We lower the max distance to 150m so the transition happens quickly.
            // Anything >150m will be at max effect.
            float maxDist = 150f;
            float tVal = Mathf.Clamp01(distance / maxDist);

            // 2. Apply Size Scaling 
            // Drastic change: 100% size at 0m -> 60% size at 150m+
            float scale = Mathf.Lerp(1.0f, 0.6f, tVal);
            arrowObj.transform.localScale = new Vector3(scale, scale, 1f);

            // 3. Apply Radius Scaling
            // Drastic change: 200px radius at 0m -> 400px radius at 150m+
            // This pushes distant markers much closer to the edge of the screen.
            float dynamicRadius = Mathf.Lerp(200f, 400f, tVal);

            // Calculate Screen Position
            float rad = angle * Mathf.Deg2Rad;
            float screenX = Mathf.Sin(rad) * dynamicRadius;
            float screenY = Mathf.Cos(rad) * dynamicRadius;

            RectTransform rt = arrowObj.GetComponent<RectTransform>();
            rt.anchoredPosition = new Vector2(screenX, screenY);

            // Reset rotation so the icon stays upright
            rt.localEulerAngles = Vector3.zero;
        }

        // --- UI Factory ---

        private void SetupOverlay(Transform hud)
        {
            // Cleanup
            Canvas existing = hud.GetComponentsInChildren<Canvas>(true)
                .FirstOrDefault(c => c != null && c.gameObject.name == "DuckNav_Overlay");
            if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

            // Main Canvas
            GameObject overlayObj = new GameObject("DuckNav_Overlay");
            overlayObj.transform.SetParent(hud, false);
            _overlayCanvas = overlayObj.AddComponent<Canvas>();
            _overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _overlayCanvas.sortingOrder = 10000;
            overlayObj.AddComponent<GraphicRaycaster>();

            // 1. Radar Container (Center)
            GameObject radarObj = new GameObject("RadarContainer");
            radarObj.transform.SetParent(overlayObj.transform, false);
            _radarContainer = radarObj.AddComponent<RectTransform>();
            _radarContainer.anchorMin = new Vector2(0.5f, 0.5f);
            _radarContainer.anchorMax = new Vector2(0.5f, 0.5f);
            _radarContainer.anchoredPosition = Vector2.zero;
        }

        private GameObject CreateRadarArrow(MapMarkerPOI poi)
        {
            GameObject arrowObj = new GameObject("ArrowUI");
            arrowObj.transform.SetParent(_radarContainer, false);

            Image img = arrowObj.AddComponent<Image>();
            img.sprite = poi.Icon;
            img.color = poi.Color;

            RectTransform rt = arrowObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(32, 32);

            // Add Text below
            GameObject textObj = new GameObject("DistText");
            textObj.transform.SetParent(arrowObj.transform, false);
            Text t = textObj.AddComponent<Text>();
            t.font = _uiFont ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.fontSize = 14;
            t.alignment = TextAnchor.UpperCenter;
            t.color = Color.white;
            t.text = "0m";

            Shadow s = textObj.AddComponent<Shadow>();
            s.effectDistance = new Vector2(1, -1);

            RectTransform textRT = t.rectTransform;
            textRT.anchorMin = new Vector2(0.5f, 0f);
            textRT.anchorMax = new Vector2(0.5f, 0f);
            textRT.pivot = new Vector2(0.5f, 1f);
            textRT.anchoredPosition = new Vector2(0, -2);
            textRT.sizeDelta = new Vector2(100, 20);

            return arrowObj;
        }

        // --- Utilities ---
        private Transform FindHudCanvas()
        {
            GameObject levelManager = GameObject.Find("LevelManager");
            if (levelManager != null)
            {
                Transform hudCanvas = levelManager.transform.Find("HUDCanvas");
                if (hudCanvas != null) return hudCanvas;
            }
            Canvas[] allCanvases = Resources.FindObjectsOfTypeAll<Canvas>();
            foreach (var c in allCanvases)
                if (c != null && c.name == "HUDCanvas") return c.transform;
            return null;
        }

        private void AdoptHudFont(Transform hud)
        {
            _uiFont = null;
            Text text = hud.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t != null && t.font != null);
            if (text != null) _uiFont = text.font;
        }
    }
}