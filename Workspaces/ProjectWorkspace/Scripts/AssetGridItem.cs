#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor.Experimental.EditorVR.Core;
using UnityEditor.Experimental.EditorVR.Data;
using UnityEditor.Experimental.EditorVR.Extensions;
using UnityEditor.Experimental.EditorVR.Handles;
using UnityEditor.Experimental.EditorVR.Helpers;
using UnityEditor.Experimental.EditorVR.Proxies;
using UnityEditor.Experimental.EditorVR.Utilities;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.InputNew;
using UnityEngine.UI;

namespace UnityEditor.Experimental.EditorVR.Workspaces
{
    sealed class AssetGridItem : DraggableListItem<AssetData, string>, IPlaceSceneObject, IUsesSpatialHash, ISetHighlight,
        IUsesViewerBody, IRayVisibilitySettings, IRequestFeedback, IUsesDirectSelection, IGetPreviewOrigin, IUsesRaycastResults
    {
        const float k_PreviewDuration = 0.1f;
        const float k_MinPreviewScale = 0.01f;
        const float k_IconPreviewScale = 0.1f;
        const float k_MaxPreviewScale = 0.2f;
        const float k_RotateSpeed = 50f;
        const float k_TransitionDuration = 0.1f;
        const float k_ScaleBump = 1.1f;
        const int k_PreviewRenderQueue = 9200;

        const int k_AutoHidePreviewVertexCount = 10000;
        const int k_HidePreviewVertexCount = 100000;

        const float k_CheckAssignDelayTime = 0.15f;
        const float k_CheckAssignDelayEndTime = 0.3f;

        [SerializeField]
        Text m_Text;

        [SerializeField]
        BaseHandle m_Handle;

        [SerializeField]
        Image m_TextPanel;

        [SerializeField]
        Renderer m_Cube;

        [SerializeField]
        Renderer m_Sphere;

        [SerializeField]
        Material m_PositiveAssignmentHighlightMaterial;

        [SerializeField]
        Material m_NegativeAssignmentHighlightMaterial;

        [HideInInspector]
        [SerializeField] // Serialized so that this remains set after cloning
        GameObject m_Icon;

        GameObject m_IconPrefab;

        [HideInInspector]
        [SerializeField] // Serialized so that this remains set after cloning
        Transform m_PreviewObjectTransform;

        [SerializeField]
        bool m_IncludeRaySelectForDrop = false;

        bool m_Setup;
        bool m_AutoHidePreview;
        Vector3 m_PreviewPrefabScale;
        Vector3 m_PreviewTargetScale;
        Vector3 m_PreviewPivotOffset;
        Bounds m_PreviewBounds;
        Transform m_PreviewObjectClone;

        Coroutine m_PreviewCoroutine;
        Coroutine m_VisibilityCoroutine;

        Material m_SphereMaterial;

        // in priority order, the types of Components that you can assign this asset to
        List<Type> m_AssignmentDependencyTypes = new List<Type>();

        List<GameObject> m_UnassignableHighlighted = new List<GameObject>();

        GameObject m_CachedDropSelection;
        float m_LastDragSelectionChange;
        IEnumerator m_BlinkingSelectionEnumerator;

        // negative float value means "unassignable" result for that object at that absolute value of time
        Dictionary<int, float> m_ObjectAssignmentChecks = new Dictionary<int, float>();

        public GameObject icon
        {
            private get { return m_Icon ? m_Icon : m_Cube.gameObject; }
            set
            {
                m_Cube.gameObject.SetActive(false);
                m_Sphere.gameObject.SetActive(false);

                if (m_IconPrefab == value) // If this GridItem already has this icon loaded, just refresh it's active state
                {
                    m_Icon.SetActive(!m_PreviewObjectTransform || m_AutoHidePreview);
                    return;
                }

                if (m_Icon)
                    ObjectUtils.Destroy(m_Icon);

                m_IconPrefab = value;
                m_Icon = ObjectUtils.Instantiate(m_IconPrefab, transform, false);
                m_Icon.transform.localPosition = Vector3.up * 0.5f;
                m_Icon.transform.localRotation = Quaternion.AngleAxis(90, Vector3.down);
                m_Icon.transform.localScale = Vector3.one;

                if (m_PreviewObjectTransform && !m_AutoHidePreview)
                    m_Icon.SetActive(false);
            }
        }

        public Material material
        {
            set
            {
                if (m_SphereMaterial)
                    ObjectUtils.Destroy(m_SphereMaterial);

                m_SphereMaterial = Instantiate(value);
                m_SphereMaterial.renderQueue = k_PreviewRenderQueue;
                m_Sphere.sharedMaterial = m_SphereMaterial;
                m_Sphere.gameObject.SetActive(true);

                m_Cube.gameObject.SetActive(false);

                if (m_Icon)
                    m_Icon.gameObject.SetActive(false);
            }
        }

        public Texture texture
        {
            set
            {
                m_Sphere.gameObject.SetActive(true);
                m_Cube.gameObject.SetActive(false);

                if (m_Icon)
                    m_Icon.gameObject.SetActive(false);

                if (!value)
                {
                    m_Sphere.sharedMaterial.mainTexture = null;
                    return;
                }

                if (m_SphereMaterial)
                    ObjectUtils.Destroy(m_SphereMaterial);

                m_SphereMaterial = new Material(Shader.Find("Standard")) { mainTexture = value };
                m_SphereMaterial.renderQueue = k_PreviewRenderQueue;
                m_Sphere.sharedMaterial = m_SphereMaterial;
            }
        }

        public Texture fallbackTexture
        {
            set
            {
                if (value)
                    value.wrapMode = TextureWrapMode.Clamp;

                m_Cube.sharedMaterial.mainTexture = value;
                m_Cube.gameObject.SetActive(true);
                m_Sphere.gameObject.SetActive(false);

                if (m_Icon)
                    m_Icon.gameObject.SetActive(false);
            }
        }

        public float scaleFactor { private get; set; }

        public override void Setup(AssetData listData)
        {
            base.Setup(listData);

            m_PreviewCoroutine = null;
            m_VisibilityCoroutine = null;
            m_AutoHidePreview = false;
            icon.transform.localScale = Vector3.one;

            // First time setup
            if (!m_Setup)
            {
                // Cube material might change, so we always instance it
                MaterialUtils.GetMaterialClone(m_Cube);

                m_Handle.dragStarted += OnDragStarted;
                m_Handle.dragging += OnDragging;
                m_Handle.dragging += OnDraggingFeedForward;
                m_Handle.dragEnded += OnDragEnded;

                m_Handle.hoverStarted += OnHoverStarted;
                m_Handle.hoverEnded += OnHoverEnded;

                m_Handle.getDropObject = GetDropObject;

                //m_AssetAssigner = new AssetAssigner(data.type);

                m_Setup = true;
            }

            InstantiatePreview();

            m_Text.text = listData.name;
        }

        public void UpdateTransforms(float scale)
        {
            scaleFactor = scale;

            // Don't scale the item while changing visibility because this would conflict with AnimateVisibility
            if (m_VisibilityCoroutine != null)
                return;

            transform.localScale = Vector3.one * scale;

            m_TextPanel.transform.localRotation = CameraUtils.LocalRotateTowardCamera(transform.parent);

            if (m_Sphere.gameObject.activeInHierarchy)
                m_Sphere.transform.Rotate(Vector3.up, k_RotateSpeed * Time.deltaTime, Space.Self);

            if (data.type == "Scene")
            {
                icon.transform.rotation =
                    Quaternion.LookRotation(icon.transform.position - CameraUtils.GetMainCamera().transform.position, Vector3.up);
            }
        }

        void InstantiatePreview()
        {
            if (m_PreviewObjectTransform)
                ObjectUtils.Destroy(m_PreviewObjectTransform.gameObject);

            if (!data.preview)
                return;

            m_PreviewObjectTransform = Instantiate(data.preview).transform;

            m_PreviewObjectTransform.position = Vector3.zero;
            m_PreviewObjectTransform.rotation = Quaternion.identity;

            m_PreviewPrefabScale = m_PreviewObjectTransform.localScale;

            // Normalize total scale to 1
            m_PreviewBounds = ObjectUtils.GetBounds(m_PreviewObjectTransform);

            // Don't show a preview if there are no renderers
            if (m_PreviewBounds.size == Vector3.zero)
            {
                ObjectUtils.Destroy(m_PreviewObjectTransform.gameObject);
                return;
            }

            // Turn off expensive render settings
            foreach (var renderer in m_PreviewObjectTransform.GetComponentsInChildren<Renderer>())
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            }

            // Turn off lights
            foreach (var light in m_PreviewObjectTransform.GetComponentsInChildren<Light>())
            {
                light.enabled = false;
            }

            m_PreviewPivotOffset = m_PreviewObjectTransform.position - m_PreviewBounds.center;
            m_PreviewObjectTransform.SetParent(transform, false);

            var maxComponent = m_PreviewBounds.size.MaxComponent();
            var scaleFactor = 1 / maxComponent;
            m_PreviewTargetScale = m_PreviewPrefabScale * scaleFactor;
            m_PreviewObjectTransform.localPosition = m_PreviewPivotOffset * scaleFactor + Vector3.up * 0.5f;

            var vertCount = 0;
            foreach (var meshFilter in m_PreviewObjectTransform.GetComponentsInChildren<MeshFilter>())
            {
                if (meshFilter.sharedMesh)
                    vertCount += meshFilter.sharedMesh.vertexCount;
            }

            foreach (var skinnedMeshRenderer in m_PreviewObjectTransform.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (skinnedMeshRenderer.sharedMesh)
                    vertCount += skinnedMeshRenderer.sharedMesh.vertexCount;
            }

            // Do not show previews over a max vert count
            if (vertCount > k_HidePreviewVertexCount)
            {
                ObjectUtils.Destroy(m_PreviewObjectTransform.gameObject);
                return;
            }

            // Auto hide previews over a smaller vert count
            if (vertCount > k_AutoHidePreviewVertexCount)
            {
                m_AutoHidePreview = true;
                m_PreviewObjectTransform.localScale = Vector3.zero;
            }
            else
            {
                m_PreviewObjectTransform.localScale = m_PreviewTargetScale;
                icon.SetActive(false);
            }
        }

        protected override void OnDragStarted(BaseHandle handle, HandleEventData eventData)
        {
            base.OnDragStarted(handle, eventData);

            var rayOrigin = eventData.rayOrigin;
            this.AddRayVisibilitySettings(rayOrigin, this, m_IncludeRaySelectForDrop, true);

            var clone = Instantiate(gameObject, transform.position, transform.rotation, transform.parent);
            var cloneItem = clone.GetComponent<AssetGridItem>();

            if (cloneItem.m_PreviewObjectTransform)
            {
                m_PreviewObjectClone = cloneItem.m_PreviewObjectTransform;

#if UNITY_EDITOR
                var originalPosition = m_PreviewObjectClone.position;
                var originalRotation = m_PreviewObjectClone.rotation;
                var originalScale = m_PreviewObjectClone.localScale;
                var restoreParent = m_PreviewObjectClone.parent;
                m_PreviewObjectClone.SetParent(null); // HACK: MergePrefab deactivates the root transform when calling ConnectGameObjectToPrefab, which is EditorVR in this case
                m_PreviewObjectClone = PrefabUtility.ConnectGameObjectToPrefab(m_PreviewObjectClone.gameObject, data.preview).transform;
                m_PreviewObjectClone.SetParent(restoreParent);
                m_PreviewObjectClone.position = originalPosition;
                m_PreviewObjectClone.rotation = originalRotation;
                m_PreviewObjectClone.localScale = originalScale;
                cloneItem.m_PreviewObjectTransform = m_PreviewObjectClone;
#endif

                cloneItem.m_Cube.gameObject.SetActive(false);

                if (cloneItem.m_Icon)
                    cloneItem.m_Icon.gameObject.SetActive(false);

                m_PreviewObjectClone.gameObject.SetActive(true);
                m_PreviewObjectClone.localScale = m_PreviewTargetScale;

                // Destroy label
                ObjectUtils.Destroy(cloneItem.m_TextPanel.gameObject);
            }

            m_DragObject = clone.transform;

            // Disable any SmoothMotion that may be applied to a cloned Asset Grid Item now referencing input device p/r/s
            var smoothMotion = clone.GetComponent<SmoothMotion>();
            if (smoothMotion != null)
                smoothMotion.enabled = false;

            // setup our assignment dependency list with any known types
            AssetDropUtils.AssignmentDependencies.TryGetValue(data.type, out m_AssignmentDependencyTypes);

            StartCoroutine(ShowGrabbedObject());
        }



        float PreviouslyFoundResult(GameObject go)
        {
            float previous;
            m_ObjectAssignmentChecks.TryGetValue(go.GetInstanceID(), out previous);
            return previous;
        }

        void SetFeedForwardHighlight(GameObject selection, Transform rayOrigin, bool assignable)
        {
            if (assignable)
            {
                // blinking green highlight = YES, object can have this asset assigned
                var mat = m_PositiveAssignmentHighlightMaterial;
                if(m_BlinkingSelectionEnumerator == null)
                    m_BlinkingSelectionEnumerator = this.SetBlinkingHighlight(selection, true, rayOrigin, mat, false);
            }
            else
            {
                // solid red highlight = NO, object can't have this asset assigned
                var mat = m_NegativeAssignmentHighlightMaterial;
                this.SetHighlight(selection, true, rayOrigin, mat);

                if (!m_UnassignableHighlighted.Contains(selection))
                    m_UnassignableHighlighted.Add(selection);
            }
        }

        void StopBlinkingHighlight(GameObject go, Transform rayOrigin)
        {
            if (m_BlinkingSelectionEnumerator != null)
            {
                this.SetBlinkingHighlight(go, false);
                m_BlinkingSelectionEnumerator = null;
            }
        }

        void OnDraggingFeedForward(BaseHandle handle, HandleEventData eventData)
        {
            var rayOrigin = eventData.rayOrigin;
            var selection = TryGetSelection(rayOrigin);

            // we've just stopped hovering something, stop any blinking highlights
            if (selection == null && m_CachedDropSelection != null)
            {
                StopBlinkingHighlight(m_CachedDropSelection, rayOrigin);
                m_BlinkingSelectionEnumerator = null;
                m_CachedDropSelection = selection;
                m_LastDragSelectionChange = Time.time;
            }
            else if (selection != null)
            {
                var time = Time.time;
                if (selection != m_CachedDropSelection)
                {
                    // changed selection - stop the blinking select if present
                    StopBlinkingHighlight(m_CachedDropSelection, rayOrigin);
                    m_CachedDropSelection = selection;
                    m_LastDragSelectionChange = time;

                    var previous = PreviouslyFoundResult(selection);

                    // we've previously found we can assign this asset here
                    if (previous > 0f)
                    {
                        SetFeedForwardHighlight(selection, rayOrigin, true);
                        return;
                    }
                    // we've previously found we can't assign this asset here
                    else if (previous < 0f)
                    {
                        return;         // it should still be highlighted anyway, so do nothing
                    }
                }

                var timeDiff = time - m_LastDragSelectionChange;
                // wait until we've hovered this object for a short delay before checking its components
                if (timeDiff > k_CheckAssignDelayTime && timeDiff < k_CheckAssignDelayEndTime)
                {
                    var assignable = CheckAssignable(selection);
                    SetFeedForwardHighlight(selection, rayOrigin, assignable);
                }

                // update any blinking highlights 
                if (m_BlinkingSelectionEnumerator != null)
                    m_BlinkingSelectionEnumerator.MoveNext();
            }
            
        }

        bool CheckAssignable(GameObject go, bool checkChildren = false)
        {
            if (!checkChildren)
            {
                foreach (Type t in m_AssignmentDependencyTypes)
                {
                    if (go.GetComponent(t) != null)
                    {
                        m_ObjectAssignmentChecks[go.GetInstanceID()] = Time.time;
                        return true;
                    }
                }
            }
            else
            {
                foreach (Type t in m_AssignmentDependencyTypes)
                {
                    if (go.GetComponentInChildren(t) != null)
                    {
                        m_ObjectAssignmentChecks[go.GetInstanceID()] = Time.time;
                        return true;
                    }

                }
            }

            m_ObjectAssignmentChecks[go.GetInstanceID()] = -Time.time;
            return false;
        }

        protected override void OnDragEnded(BaseHandle handle, HandleEventData eventData)
        {
            // turn off all solid "can't assign here" highlights
            foreach (var go in m_UnassignableHighlighted)
            {
                this.SetHighlight(go, false, eventData.rayOrigin, null, true);
            }

            m_ObjectAssignmentChecks.Clear();
            m_UnassignableHighlighted.Clear();

            var gridItem = m_DragObject.GetComponent<AssetGridItem>();

            var rayOrigin = eventData.rayOrigin;
            this.RemoveRayVisibilitySettings(rayOrigin, this);

            if (!this.IsOverShoulder(eventData.rayOrigin))
            {
                var previewObjectTransform = gridItem.m_PreviewObjectTransform;
                if (previewObjectTransform)
                {
                    Undo.RegisterCreatedObjectUndo(previewObjectTransform.gameObject, "Place Scene Object");
                    this.PlaceSceneObject(previewObjectTransform, m_PreviewPrefabScale);
                }
                else
                {
                    HandleAssetDropByType(rayOrigin, gridItem);
                }
            }

            StartCoroutine(HideGrabbedObject(m_DragObject.gameObject, gridItem.m_Cube));
            base.OnDragEnded(handle, eventData);
        }

        void HandleAssetDropByType(Transform rayOrigin, AssetGridItem gridItem)
        {
            switch (data.type)
            {
                case "AnimationClip":
                    PlaceAnimationClip(rayOrigin, data);
                    break;
                case "AudioClip":
                    PlaceAudioClip(rayOrigin, data);
                    break;
                case "VideoClip":
                    PlaceVideoClip(rayOrigin, data);
                    break;
                case "Font":
                    PlaceFont(rayOrigin, data);
                    break;
                case "PhysicMaterial":
                    PlacePhysicMaterial(rayOrigin, data);
                    break;
                case "Material":
                    PlaceMaterial(rayOrigin, data);
                    break;
                case "Script":
                    PlaceScript(rayOrigin, data);
                    break;
                case "Shader":
                    PlaceShader(rayOrigin, data);
                    break;
                case "Prefab":
                case "Model":
                    PlaceModelOrPrefab(gridItem, data);
                    break;
            }
        }

        void PlaceAudioClip(Transform rayOrigin, AssetData data)
        {
            var selection = TryGetSelection(rayOrigin);
            if (selection != null)
                AssetDropUtils.AttachAudioClip(selection, data);
        }

        void PlaceAnimationClip(Transform rayOrigin, AssetData data)
        {
            var selection = TryGetSelection(rayOrigin);
            if (selection != null)
                AssetDropUtils.AssignAnimationClip(selection, data);
        }

        void PlaceVideoClip(Transform rayOrigin, AssetData data)
        {
            var selection = TryGetSelection(rayOrigin);
            if (selection != null)
                AssetDropUtils.AttachVideoClip(selection, data);
        }

        void PlaceModelOrPrefab(AssetGridItem gridItem, AssetData data)
        {
#if UNITY_EDITOR
            var go = (GameObject)PrefabUtility.InstantiatePrefab(data.asset);
            var transform = go.transform;
            transform.position = gridItem.transform.position;
            transform.rotation = MathUtilsExt.ConstrainYawRotation(gridItem.transform.rotation);
#else
            var go = (GameObject)Instantiate(data.asset, gridItem.transform.position, gridItem.transform.rotation);
#endif

            this.AddToSpatialHash(go);
            Undo.RegisterCreatedObjectUndo(go, "Project Workspace");
        }

        void PlaceFont(Transform rayOrigin, AssetData data)
        {
            var selection = TryGetSelection(rayOrigin);
            if (selection != null)
                AssetDropUtils.AssignFontOnChildren(selection, data);
        }

        void PlaceScript(Transform rayOrigin, AssetData data)
        {
            var selection = TryGetSelection(rayOrigin);
            if (selection != null)
                AssetDropUtils.AttachScript(selection, data);
        }

        void PlaceMaterial(Transform rayOrigin, AssetData data)
        {
            var selection = TryGetSelection(rayOrigin);
            if (selection != null)
                AssetDropUtils.AssignMaterial(selection, data);
        }

        void PlacePhysicMaterial(Transform rayOrigin, AssetData data)
        {
            var selection = TryGetSelection(rayOrigin);
            if (selection != null)
                AssetDropUtils.AssignColliderPhysicMaterial(selection, data);
        }

        void PlaceShader(Transform rayOrigin, AssetData data)
        {
            var selection = TryGetSelection(rayOrigin);
            if (selection != null)
                AssetDropUtils.AssignMaterialShader(selection, data);
        }


        GameObject TryGetSelection(Transform rayOrigin, bool includeRays = false)
        {
            GameObject selection = null;
            var directSelections = this.GetDirectSelection();
            if (directSelections != null)
                directSelections.TryGetValue(rayOrigin, out selection);

            if (selection == null && includeRays)
                selection = this.GetFirstGameObject(rayOrigin);

            return selection;
        }

        GameObject TryGetSelection(Transform rayOrigin)
        {
            return TryGetSelection(rayOrigin, m_IncludeRaySelectForDrop);
        }

        void OnHoverStarted(BaseHandle handle, HandleEventData eventData)
        {
            if (m_PreviewObjectTransform && gameObject.activeInHierarchy)
            {
                if (m_AutoHidePreview)
                {
                    this.StopCoroutine(ref m_PreviewCoroutine);
                    m_PreviewCoroutine = StartCoroutine(AnimatePreview(false));
                }
                else
                {
                    m_PreviewObjectTransform.localScale = m_PreviewTargetScale * k_ScaleBump;
                }
            }

            base.OnHoverStart(handle, eventData);
            ShowGrabFeedback(this.RequestNodeFromRayOrigin(eventData.rayOrigin));
        }

        void OnHoverEnded(BaseHandle handle, HandleEventData eventData)
        {
            if (m_PreviewObjectTransform && gameObject.activeInHierarchy)
            {
                if (m_AutoHidePreview)
                {
                    this.StopCoroutine(ref m_PreviewCoroutine);
                    m_PreviewCoroutine = StartCoroutine(AnimatePreview(true));
                }
                else
                {
                    m_PreviewObjectTransform.localScale = m_PreviewTargetScale;
                }
            }

            HideGrabFeedback();
        }

        IEnumerator AnimatePreview(bool @out)
        {
            icon.SetActive(true);
            m_PreviewObjectTransform.gameObject.SetActive(true);

            var iconTransform = icon.transform;
            var currentIconScale = iconTransform.localScale;
            var targetIconScale = @out ? Vector3.one : Vector3.zero;

            var currentPreviewScale = m_PreviewObjectTransform.localScale;
            var targetPreviewScale = @out ? Vector3.zero : m_PreviewTargetScale;

            var startTime = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - startTime < k_PreviewDuration)
            {
                var t = (Time.realtimeSinceStartup - startTime) / k_PreviewDuration;

                icon.transform.localScale = Vector3.Lerp(currentIconScale, targetIconScale, t);
                m_PreviewObjectTransform.transform.localScale = Vector3.Lerp(currentPreviewScale, targetPreviewScale, t);
                yield return null;
            }

            m_PreviewObjectTransform.transform.localScale = targetPreviewScale;
            icon.transform.localScale = targetIconScale;

            m_PreviewObjectTransform.gameObject.SetActive(!@out);
            icon.SetActive(@out);

            m_PreviewCoroutine = null;
        }

        public void SetVisibility(bool visible, Action<AssetGridItem> callback = null)
        {
            this.StopCoroutine(ref m_VisibilityCoroutine);
            m_VisibilityCoroutine = StartCoroutine(AnimateVisibility(visible, callback));
        }

        IEnumerator AnimateVisibility(bool visible, Action<AssetGridItem> callback)
        {
            var currentTime = 0f;

            // Item should always be at a scale of zero before becoming visible
            if (visible)
                transform.localScale = Vector3.zero;

            var currentScale = transform.localScale;
            var targetScale = visible ? Vector3.one * scaleFactor : Vector3.zero;

            while (currentTime < k_TransitionDuration)
            {
                currentTime += Time.deltaTime;
                transform.localScale = Vector3.Lerp(currentScale, targetScale, currentTime / k_TransitionDuration);
                yield return null;
            }

            transform.localScale = targetScale;

            if (callback != null)
                callback(this);

            m_VisibilityCoroutine = null;
        }

        object GetDropObject(BaseHandle handle)
        {
            return data.asset;
        }

        void OnDestroy()
        {
            if (m_SphereMaterial)
                ObjectUtils.Destroy(m_SphereMaterial);

            ObjectUtils.Destroy(m_Cube.sharedMaterial);
        }

        // Animate the LocalScale of the asset towards a common/unified scale
        // used when the asset is magnetized/attached to the proxy, after grabbing it from the asset grid
        IEnumerator ShowGrabbedObject()
        {
            var currentLocalScale = m_DragObject.localScale;
            var currentPreviewOffset = Vector3.zero;
            var currentPreviewRotationOffset = Quaternion.identity;

            if (m_PreviewObjectClone)
                currentPreviewOffset = m_PreviewObjectClone.localPosition;

            var currentTime = 0f;
            var currentVelocity = 0f;
            const float kDuration = 1f;

            var targetScale = Vector3.one * k_IconPreviewScale;
            var pivotOffset = Vector3.zero;
            var rotationOffset = Quaternion.AngleAxis(30, Vector3.right);
            if (m_PreviewObjectClone)
            {
                var viewerScale = this.GetViewerScale();
                var maxComponent = m_PreviewBounds.size.MaxComponent() / viewerScale;
                targetScale = Vector3.one * maxComponent;

                // Object will preview at the same size when grabbed
                var previewExtents = m_PreviewBounds.extents / viewerScale;
                pivotOffset = m_PreviewPivotOffset / viewerScale;

                // If bounds are greater than offset, set to bounds
                if (previewExtents.y > pivotOffset.y)
                    pivotOffset.y = previewExtents.y;

                if (previewExtents.z > pivotOffset.z)
                    pivotOffset.z = previewExtents.z;

                if (maxComponent < k_MinPreviewScale)
                {
                    // Object will be preview at the minimum scale
                    targetScale = Vector3.one * k_MinPreviewScale;
                    pivotOffset = pivotOffset * scaleFactor + (Vector3.up + Vector3.forward) * 0.5f * k_MinPreviewScale;
                }

                if (maxComponent > k_MaxPreviewScale)
                {
                    // Object will be preview at the maximum scale
                    targetScale = Vector3.one * k_MaxPreviewScale;
                    pivotOffset = pivotOffset * scaleFactor + (Vector3.up + Vector3.forward) * 0.5f * k_MaxPreviewScale;
                }
            }

            while (currentTime < kDuration - 0.05f)
            {
                if (m_DragObject == null)
                    yield break; // Exit coroutine if m_GrabbedObject is destroyed before the loop is finished

                currentTime = MathUtilsExt.SmoothDamp(currentTime, kDuration, ref currentVelocity, 0.5f, Mathf.Infinity, Time.deltaTime);
                m_DragObject.localScale = Vector3.Lerp(currentLocalScale, targetScale, currentTime);

                if (m_PreviewObjectClone)
                {
                    m_PreviewObjectClone.localPosition = Vector3.Lerp(currentPreviewOffset, pivotOffset, currentTime);
                    m_PreviewObjectClone.localRotation = Quaternion.Lerp(currentPreviewRotationOffset, rotationOffset, currentTime); // Compensate for preview origin rotation
                }

                yield return null;
            }

            m_DragObject.localScale = targetScale;
        }

        static IEnumerator HideGrabbedObject(GameObject itemToHide, Renderer cubeRenderer)
        {
            var itemTransform = itemToHide.transform;
            var currentScale = itemTransform.localScale;
            var targetScale = Vector3.zero;
            var transitionAmount = Time.deltaTime;
            var transitionAddMultiplier = 6;
            while (transitionAmount < 1)
            {
                itemTransform.localScale = Vector3.Lerp(currentScale, targetScale, transitionAmount);
                transitionAmount += Time.deltaTime * transitionAddMultiplier;
                yield return null;
            }

            cubeRenderer.sharedMaterial = null; // Drop material so it won't be destroyed (shared with cube in list)
            ObjectUtils.Destroy(itemToHide);
        }

        void ShowGrabFeedback(Node node)
        {
            var request = (ProxyFeedbackRequest)this.GetFeedbackRequestObject(typeof(ProxyFeedbackRequest));
            request.control = VRInputDevice.VRControl.Trigger1;
            request.node = node;
            request.tooltipText = "Grab";
            this.AddFeedbackRequest(request);
        }

        void HideGrabFeedback()
        {
            this.ClearFeedbackRequests();
        }

        public void OnResetDirectSelectionState() {}
    }
}
#endif
