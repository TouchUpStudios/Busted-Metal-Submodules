using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using Deform;
using Unity.Mathematics;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;
using float3 = Unity.Mathematics.float3;

namespace DeformEditor
{
    [CustomEditor(typeof(SurfaceDeformer))]
    public class SurfaceDeformerEditor : DeformerEditor
    {
        private MeshFilter newMesh;
        private float newDistanceMin;

        private float3 handleScale = Vector3.one;
        private Tool activeTool = Tool.None;

        enum MouseDragState
        {
            NotActive,
            Eligible,
            InProgress
        };

        private MouseDragState mouseDragState = MouseDragState.NotActive;
        private Vector2 mouseDownPosition;
        private int previousSelectionCount = 0;

        // Positions of selected points before a rotate or scale begins
        private List<float3> selectedOriginalPositions = new List<float3>();
        
        // Positions and resolution before a resize
        private float3[] cachedResizePositions = new float3[0];
        
        [SerializeField] private List<int> selectedIndices = new List<int>();

        private static class Content
        {
            public static readonly GUIContent Resolution  = new GUIContent(text: "Resolution", tooltip: "Per axis control point counts, the higher the resolution the more splits");
            public static readonly GUIContent Mesh        = new GUIContent(text: "Mesh", tooltip: "Reference Mesh");
            public static readonly GUIContent DistanceMin = new GUIContent(text: "Distance Min", tooltip: "Minimum distance between mesh points");
            public static readonly GUIContent StopEditing = new GUIContent(text: "Stop Editing Control Points", tooltip: "Restore normal transform tools\n\nShortcut: Escape");
        }

        private class Properties
        {
            public SerializedProperty Resolution;
            public SerializedProperty Mesh;
            public SerializedProperty DistanceMin;

            public Properties(SerializedObject obj)
            {
                Resolution = obj.FindProperty("resolution");
                Mesh = obj.FindProperty("meshFilter");
                DistanceMin = obj.FindProperty("distanceMin");
            }
        }

        private Properties properties;

        protected override void OnEnable()
        {
            base.OnEnable();

            properties = new Properties(serializedObject);
            
            SurfaceDeformer surfaceDeformer = ((SurfaceDeformer) target);
            newMesh = surfaceDeformer.Mesh;
            newDistanceMin = surfaceDeformer.DistanceMin;
            CacheResizePositionsFromChange();
            
            Undo.undoRedoPerformed += UndoRedoPerformed;
        }

        private void UndoRedoPerformed()
        {
            SurfaceDeformer surfaceDeformer = ((SurfaceDeformer) target);
            newMesh = surfaceDeformer.Mesh;
            newDistanceMin = surfaceDeformer.DistanceMin;
            CacheResizePositionsFromChange();
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            SurfaceDeformer surfaceDeformer = ((SurfaceDeformer) target);

            serializedObject.UpdateIfRequiredOrScript();

            EditorGUI.BeginChangeCheck();

            newMesh = (MeshFilter) EditorGUILayout.ObjectField("Mesh", newMesh, typeof(MeshFilter), true);
            surfaceDeformer.SetMeshFilter(newMesh);

            newDistanceMin = Mathf.Max(EditorGUILayout.FloatField(Content.DistanceMin, newDistanceMin), 0);
            surfaceDeformer.SetDistanceMin(newDistanceMin);

            EditorGUILayout.LabelField(string.Format("Control Points: {0}", surfaceDeformer.ControlPoints.Length));

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(target, "Update Surface");
                surfaceDeformer.GenerateControlPoints();
                selectedIndices.Clear();
            }

            if (GUILayout.Button("Reset Surface Points"))
            {
                Undo.RecordObject(target, "Reset Surface Points");
                surfaceDeformer.GenerateControlPoints();
                selectedIndices.Clear();
                
                CacheResizePositionsFromChange();
            }

            serializedObject.ApplyModifiedProperties();

            EditorApplication.QueuePlayerLoopUpdate();
        }

        public override void OnSceneGUI()
        {
            base.OnSceneGUI();


            SurfaceDeformer surface = target as SurfaceDeformer;
            
            if(surface.ControlPointsBase == null || surface.ControlPoints == null){ 
                return;
            }

            Transform transform = surface.transform;
            float3[] controlPoints = surface.ControlPoints;
            Event e = Event.current;

            using (new Handles.DrawingScope(transform.localToWorldMatrix))
            {
                var cachedZTest = Handles.zTest;

                // Restore the original z test value now we're done with our drawing
                Handles.zTest = cachedZTest;

                for (int i = 0; i < surface.ControlPoints.Length; i++)
                {
                    var controlPointHandleID = GUIUtility.GetControlID("SurfaceDeformerControlPoint".GetHashCode(), FocusType.Passive);
                    var activeColor = DeformEditorSettings.SolidHandleColor;
                    var controlPointIndex = i;

                    if (GUIUtility.hotControl == controlPointHandleID || selectedIndices.Contains(controlPointIndex))
                    {
                        activeColor = Handles.selectedColor;
                    }
                    else if (HandleUtility.nearestControl == controlPointHandleID)
                    {
                        activeColor = Handles.preselectionColor;
                    }

                    if (e.type == EventType.MouseDown && HandleUtility.nearestControl == controlPointHandleID && e.button == 0 && MouseActionAllowed)
                    {
                        BeginSelectionChangeRegion();
                        GUIUtility.hotControl = controlPointHandleID;
                        GUIUtility.keyboardControl = controlPointHandleID;
                        e.Use();

                        bool modifierKeyPressed = e.control || e.shift || e.command;

                        if (modifierKeyPressed && selectedIndices.Contains(controlPointIndex))
                        {
                            // Pressed a modifier key so toggle the selection
                            selectedIndices.Remove(controlPointIndex);
                        }
                        else
                        {
                            if (!modifierKeyPressed)
                            {
                                selectedIndices.Clear();
                            }

                            if (!selectedIndices.Contains(controlPointIndex))
                            {
                                selectedIndices.Add(controlPointIndex);
                            }
                        }

                        EndSelectionChangeRegion();
                    }

                    if (Tools.current != Tool.None && selectedIndices.Count != 0)
                    {
                        // If the user changes tool, change our internal mode to match but disable the corresponding Unity tool
                        // (e.g. they hit W key or press on the Rotate Tool button on the top left toolbar) 
                        activeTool = Tools.current;
                        Tools.current = Tool.None;
                    }

                    using (new Handles.DrawingScope(activeColor))
                    {
                        var position = controlPoints[controlPointIndex];
                        var size = HandleUtility.GetHandleSize(position) * DeformEditorSettings.ScreenspaceLatticeHandleCapSize;
                        Handles.DotHandleCap(controlPointHandleID, position, Quaternion.identity, size, e.type);                                
                    }
                }
            }

            var defaultControl = DeformUnityObjectSelection.DisableSceneViewObjectSelection();

            if (selectedIndices.Count != 0)
            {
                var currentPivotPosition = float3.zero;

                if (Tools.pivotMode == PivotMode.Center)
                {
                    // Get the average position
                    foreach (var index in selectedIndices)
                    {
                        currentPivotPosition += controlPoints[index];
                    }

                    currentPivotPosition /= selectedIndices.Count;
                }
                else
                {
                    // Match the scene view behaviour that Pivot mode uses the last selected object as pivot
                    currentPivotPosition = controlPoints[selectedIndices.Last()];
                }

                float3 handlePosition = transform.TransformPoint(currentPivotPosition);

                if (e.type == EventType.MouseDown)
                {
                    // Potentially started interacting with a handle so reset everything
                    handleScale = Vector3.one;
                    // Make sure we cache the positions just before the interaction changes them
                    CacheOriginalPositions();
                }

                var originalPivotPosition = float3.zero;

                if (Tools.pivotMode == PivotMode.Center)
                {
                    // Get the average position
                    foreach (var originalPosition in selectedOriginalPositions)
                    {
                        originalPivotPosition += originalPosition;
                    }

                    originalPivotPosition /= selectedIndices.Count;
                }
                else
                {
                    // Match the scene view behaviour that Pivot mode uses the last selected object as pivot
                    originalPivotPosition = selectedOriginalPositions.Last();
                }

                var handleRotation = transform.rotation;
                if (Tools.pivotRotation == PivotRotation.Global)
                {
                    handleRotation = Quaternion.identity;
                }

                if (activeTool == Tool.Move)
                {
                    EditorGUI.BeginChangeCheck();
                    float3 newPosition = Handles.PositionHandle(handlePosition, handleRotation);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Update Surface");

                        var delta = newPosition - handlePosition;
                        delta = transform.InverseTransformVector(delta);
                        foreach (var selectedIndex in selectedIndices)
                        {
                            controlPoints[selectedIndex] += delta;
                        }
                        
                        CacheResizePositionsFromChange();
                    }
                }
                else if (activeTool == Tool.Rotate)
                {
                    EditorGUI.BeginChangeCheck();
                    quaternion newRotation = Handles.RotationHandle(handleRotation, handlePosition);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Update Surface");

                        for (var index = 0; index < selectedIndices.Count; index++)
                        {
                            if (Tools.pivotRotation == PivotRotation.Global)
                            {
                                controlPoints[selectedIndices[index]] = originalPivotPosition + (float3) transform.InverseTransformDirection(mul(newRotation, transform.TransformDirection(selectedOriginalPositions[index] - originalPivotPosition)));
                            }
                            else
                            {
                                controlPoints[selectedIndices[index]] = originalPivotPosition + mul(mul(inverse(handleRotation), newRotation), (selectedOriginalPositions[index] - originalPivotPosition));
                            }
                        }
                        
                        CacheResizePositionsFromChange();
                    }
                }
                else if (activeTool == Tool.Scale)
                {
                    var size = HandleUtility.GetHandleSize(handlePosition);
                    EditorGUI.BeginChangeCheck();
                    handleScale = Handles.ScaleHandle(handleScale, handlePosition, handleRotation, size);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Update Surface");

                        for (var index = 0; index < selectedIndices.Count; index++)
                        {
                            if (Tools.pivotRotation == PivotRotation.Global)
                            {
                                controlPoints[selectedIndices[index]] = originalPivotPosition + (float3) transform.InverseTransformDirection(handleScale * transform.TransformDirection(selectedOriginalPositions[index] - originalPivotPosition));
                            }
                            else
                            {
                                controlPoints[selectedIndices[index]] = originalPivotPosition + handleScale * (selectedOriginalPositions[index] - originalPivotPosition);
                            }
                        }
                        
                        CacheResizePositionsFromChange();
                    }
                }

                Handles.BeginGUI();
                if (GUI.Button(new Rect((EditorGUIUtility.currentViewWidth - 200) / 2, SceneView.currentDrawingSceneView.position.height - 60, 200, 30), Content.StopEditing))
                {
                    DeselectAll();
                }

                Handles.EndGUI();
            }

            if (e.button == 0) // Left Mouse Button
            {
                if (e.type == EventType.MouseDown && HandleUtility.nearestControl == defaultControl && MouseActionAllowed)
                {
                    mouseDownPosition = e.mousePosition;
                    mouseDragState = MouseDragState.Eligible;
                }
                else if (e.type == EventType.MouseDrag && mouseDragState == MouseDragState.Eligible)
                {
                    mouseDragState = MouseDragState.InProgress;
                    SceneView.currentDrawingSceneView.Repaint();
                }
                else if (GUIUtility.hotControl == 0 &&
                         (e.type == EventType.MouseUp
                          || (mouseDragState == MouseDragState.InProgress && e.rawType == EventType.MouseUp))) // Have they released the mouse outside the scene view while doing marquee select?
                {
                    if (mouseDragState == MouseDragState.InProgress)
                    {
                        var mouseUpPosition = e.mousePosition;

                        Rect marqueeRect = Rect.MinMaxRect(Mathf.Min(mouseDownPosition.x, mouseUpPosition.x),
                            Mathf.Min(mouseDownPosition.y, mouseUpPosition.y),
                            Mathf.Max(mouseDownPosition.x, mouseUpPosition.x),
                            Mathf.Max(mouseDownPosition.y, mouseUpPosition.y));

                        BeginSelectionChangeRegion();

                        if (!e.shift && !e.control && !e.command)
                        {
                            selectedIndices.Clear();
                        }

                        for (var index = 0; index < controlPoints.Length; index++)
                        {
                            Camera camera = SceneView.currentDrawingSceneView.camera;
                            var screenPoint = DeformEditorGUIUtility.WorldToGUIPoint(camera, transform.TransformPoint(controlPoints[index]));

                            if (screenPoint.z < 0)
                            {
                                // Don't consider points that are behind the camera
                                continue;
                            }

                            if (marqueeRect.Contains(screenPoint))
                            {
                                if (e.control || e.command) // Remove selection
                                {
                                    selectedIndices.Remove(index);
                                }
                                else
                                {
                                    selectedIndices.Add(index);
                                }
                            }
                        }

                        EndSelectionChangeRegion();
                    }
                    else
                    {
                        if (selectedIndices.Count == 0) // This shouldn't be called if you have any points selected (we want to allow you to deselect the points)
                        {
                            DeformUnityObjectSelection.AttemptMouseUpObjectSelection();
                        }
                        else
                        {
                            DeselectAll();
                        }
                    }

                    mouseDragState = MouseDragState.NotActive;
                }
            }

            if (e.type == EventType.Repaint && mouseDragState == MouseDragState.InProgress)
            {
                var mouseUpPosition = e.mousePosition;

                Rect marqueeRect = Rect.MinMaxRect(Mathf.Min(mouseDownPosition.x, mouseUpPosition.x),
                    Mathf.Min(mouseDownPosition.y, mouseUpPosition.y),
                    Mathf.Max(mouseDownPosition.x, mouseUpPosition.x),
                    Mathf.Max(mouseDownPosition.y, mouseUpPosition.y));
                DeformUnityObjectSelection.DrawUnityStyleMarquee(marqueeRect);
                SceneView.RepaintAll();
            }

            // If the surface is visible, override Unity's built-in Select All so that it selects all control points 
            if (DeformUnityObjectSelection.SelectAllPressed)
            {
                BeginSelectionChangeRegion();
                selectedIndices.Clear();
                for (int i = 0; i < surface.ControlPoints.Length; i++)
                {
                    selectedIndices.Add(i);
                }

                EndSelectionChangeRegion();

                e.Use();
            }

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                DeselectAll();
            }

            EditorApplication.QueuePlayerLoopUpdate();
        }

        private void DeselectAll()
        {
            BeginSelectionChangeRegion();
            selectedIndices.Clear();
            EndSelectionChangeRegion();
        }

        private void BeginSelectionChangeRegion()
        {
            Undo.RecordObject(this, "Selection Change");
            previousSelectionCount = selectedIndices.Count;
        }

        private void EndSelectionChangeRegion()
        {
            if (selectedIndices.Count != previousSelectionCount)
            {
                if (selectedIndices.Count != 0 && previousSelectionCount == 0 && Tools.current == Tool.None) // Is this our first selection?
                {
                    // Make sure when we start selecting control points we actually have a useful tool equipped
                    activeTool = Tool.Move;
                }
                else if (selectedIndices.Count == 0 && previousSelectionCount != 0)
                {
                    // If we have deselected we should probably restore the active tool from before
                    Tools.current = activeTool;
                }
                
                // Selected positions have changed so make sure we're up to date
                CacheOriginalPositions();

                // Different UI elements may be visible depending on selection count, so redraw when it changes
                Repaint();
            }
        }

        private void CacheOriginalPositions()
        {
            // Cache the selected control point positions before the interaction, so that all handle
            // transformations are done using the original values rather than compounding error each frame
            var surfaceDeformer = (target as SurfaceDeformer);
            float3[] controlPoints = surfaceDeformer.ControlPoints;
            selectedOriginalPositions.Clear();
            foreach (int selectedIndex in selectedIndices)
            {
                selectedOriginalPositions.Add(controlPoints[selectedIndex]);
            }
        }

        private void CacheResizePositionsFromChange()
        {
            var surfaceDeformer = (target as SurfaceDeformer);
            float3[] controlPoints = surfaceDeformer.ControlPoints;
            cachedResizePositions = new float3[controlPoints.Length];
            controlPoints.CopyTo(cachedResizePositions, 0);
        }

        private static bool MouseActionAllowed
        {
            get
            {
                if (Event.current.alt) return false;

                return true;
            }
        }
    }
}