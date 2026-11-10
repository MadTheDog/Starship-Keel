using Manager;
using UnityEngine;

namespace Sevo.Framework.Runtime
{
    // A non-interactive badge that borrows the stock standalone ore sprite.
    internal sealed class TerrainDepositBadge
    {
        private const int MaxVisible = 12;
        // In the stock 256px sprite, the visible 140 × 161px silhouette is off-center.
        private const float PaddedOreSize = 44f;
        private static readonly Vector2 OreSpriteCenter = new Vector2(119f / 256f, 139f / 256f);
        private const float VisibleOreWidth = 24f;
        private const float VisibleOreHeight = 28f;
        private static readonly Color Ink = new Color(0.105f, 0.12f, 0.105f);
        private readonly Rect[] occupied = new Rect[MaxVisible];
        private GUIStyle? symbol;
        private static readonly int OreSpriteProperty = Shader.PropertyToID("_OreSprite");
        private Texture2D? oreSprite;
        private int count;
        private float scale;
        private float fade;

        internal void Begin(float stockFade)
        {
            scale = Mathf.Clamp(Screen.height / 1080f, 0.85f, 2f);
            fade = Mathf.Clamp01(stockFade);
            count = 0;
            var material = AssetRepoUI.Material_trackingWidget;
            oreSprite = material != null && material.HasProperty(OreSpriteProperty)
                && material.GetTexture(OreSpriteProperty) is Texture2D texture
                && texture.width > 0 && texture.height > 0 ? texture : null;
            symbol ??= new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false };
            symbol.fontSize = Mathf.RoundToInt(20f * scale);
            symbol.normal.textColor = Color.white;
        }

        internal bool AtCapacity => count == MaxVisible;

        internal void Draw(Vector3 point, GUIContent label, Color tint)
        {
            if (AtCapacity || fade <= 0f || tint.a <= 0f) return;
            var style = symbol!;
            float iconWidth = oreSprite != null ? VisibleOreWidth * scale : 0f;
            float iconHeight = oreSprite != null ? VisibleOreHeight * scale : 0f;
            float gap = oreSprite != null ? 2f * scale : 0f;
            float height = 25f * scale;
            float capsuleWidth = Mathf.Max(32f * scale, style.CalcSize(label).x + 10f * scale);
            float width = iconWidth + gap + capsuleWidth;
            float x = point.x - width * 0.5f;
            float centerY = Screen.height - point.y;
            float y = centerY - height * 0.5f;
            float visibleHeight = Mathf.Max(height, iconHeight);
            var bounds = new Rect(x, centerY - visibleHeight * 0.5f, width, visibleHeight);
            if (bounds.xMin < 0f || bounds.xMax > Screen.width || bounds.yMin < 0f || bounds.yMax > Screen.height)
                return;

            float separation = 4f * scale;
            var reserve = Rect.MinMaxRect(bounds.xMin - separation, bounds.yMin - separation,
                bounds.xMax + separation, bounds.yMax + separation);
            for (int i = 0; i < count; i++)
                if (reserve.Overlaps(occupied[i])) return;
            occupied[count++] = reserve;

            float alpha = tint.a * fade;
            Color foreground = tint;
            foreground.a = alpha;
            var outline = new Color(Ink.r, Ink.g, Ink.b, alpha * 0.96f);
            var fill = Color.Lerp(Ink, foreground, 0.13f);
            fill.a = alpha * 0.93f;
            var capsule = new Rect(x + iconWidth + gap, y, capsuleWidth, height);
            GUI.color = Color.white;
            DrawCapsule(capsule, outline, 8f * scale);
            float border = Mathf.Max(1f, scale);
            var inside = Rect.MinMaxRect(capsule.xMin + border, capsule.yMin + border,
                capsule.xMax - border, capsule.yMax - border);
            DrawCapsule(inside, fill, 7f * scale);

            if (oreSprite != null)
            {
                float paddedSize = PaddedOreSize * scale;
                var icon = new Rect(x + iconWidth * 0.5f - paddedSize * OreSpriteCenter.x,
                    centerY - paddedSize * OreSpriteCenter.y, paddedSize, paddedSize);
                GUI.color = foreground;
                GUI.DrawTexture(icon, oreSprite, ScaleMode.ScaleToFit, true);
            }
            // A one-pixel shadow provides the heavier weight of the stock ore symbol
            // without requiring the game's font module or changing the active GUI skin.
            var measured = style.CalcSize(label);
            var text = new Rect(capsule.center.x - measured.x * 0.5f,
                capsule.center.y - measured.y * 0.5f, measured.x, measured.y);
            GUI.color = outline;
            GUI.Label(new Rect(text.x + scale, text.y + scale, text.width, text.height), label, style);
            GUI.color = foreground;
            GUI.Label(text, label, style);
            GUI.Label(new Rect(text.x + 0.45f * scale, text.y, text.width, text.height), label, style);
        }

        private static void DrawCapsule(Rect rect, Color tint, float radius)
            => GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, tint, 0f, radius);
    }
}
