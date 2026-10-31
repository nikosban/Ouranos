using UnityEngine;

namespace Ouranos.Interaction
{
    /// <summary>
    /// Provisional placeholder: draws the drag rectangle of the gesture in progress as a filled screen box with a border.
    /// Reads <see cref="PlayerCommands.IsDragging"/> and <see cref="PlayerCommands.DragRect"/> only. Final appearance is
    /// an owner decision.
    /// </summary>
    public class SelectionBoxView : MonoBehaviour
    {
        public PlayerCommands Commands;

        [Tooltip("Rectangle fill colour (provisional placeholder).")]
        public Color FillColor = new Color(0.9f, 0.9f, 0.9f, 0.15f);
        [Tooltip("Rectangle border colour (provisional placeholder).")]
        public Color BorderColor = new Color(0.9f, 0.9f, 0.9f, 0.9f);
        [Tooltip("Rectangle border width (px).")]
        [Range(1f, 10f)] public float BorderWidth = 2f;

        void OnGUI()
        {
            if (Commands == null || !Commands.IsDragging || Event.current.type != EventType.Repaint) return;

            // DragRect has its origin at the bottom-left; IMGUI at the top-left.
            Rect drag = Commands.DragRect;
            var box = new Rect(drag.xMin, Screen.height - drag.yMax, drag.width, drag.height);
            float border = Mathf.Min(BorderWidth, box.width * 0.5f, box.height * 0.5f);
            Fill(box, FillColor);
            Fill(new Rect(box.xMin, box.yMin, box.width, border), BorderColor);
            Fill(new Rect(box.xMin, box.yMax - border, box.width, border), BorderColor);
            Fill(new Rect(box.xMin, box.yMin + border, border, box.height - 2f * border), BorderColor);
            Fill(new Rect(box.xMax - border, box.yMin + border, border, box.height - 2f * border), BorderColor);
        }

        static void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
}
