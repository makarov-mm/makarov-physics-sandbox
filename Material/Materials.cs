using MakarovPhysicsSandbox.Physics;
using System.Numerics;

namespace MakarovPhysicsSandbox.Material;

public static class Materials
{
    public static readonly MaterialDefinition[] All =
    [
        new(MaterialId.Custom, "Custom", 1.00f, 0.50f, 0.30f, new Vector3(0.80f, 0.80f, 0.80f), false, 7.5f, 8, 0.70f, 0.05f, 0.0f),
        new(MaterialId.Wood, "Wood", 0.65f, 0.55f, 0.22f, new Vector3(0.55f, 0.32f, 0.14f), true, 6.5f, 8, 1.00f, 0.08f, 0.0f),
        new(MaterialId.Metal, "Metal", 3.20f, 0.42f, 0.12f, new Vector3(0.62f, 0.64f, 0.68f), false, 12.0f, 8, 0.00f, 1.00f, 0.0f),
        new(MaterialId.Rubber, "Rubber", 1.10f, 1.25f, 0.95f, new Vector3(0.08f, 0.08f, 0.10f), false, 12.0f, 8, 0.35f, 0.02f, 0.0f),
        new(MaterialId.Glass, "Glass", 1.20f, 0.18f, 0.18f, new Vector3(0.70f, 0.92f, 1.00f), true, 3.2f, 12, 0.05f, 0.01f, 0.0f),
        new(MaterialId.Stone, "Stone", 2.60f, 0.85f, 0.08f, new Vector3(0.48f, 0.48f, 0.46f), true, 10.0f, 8, 0.00f, 0.02f, 0.0f),
        new(MaterialId.Foam, "Foam", 0.25f, 0.75f, 0.35f, new Vector3(0.95f, 0.88f, 0.45f), false, 5.0f, 8, 1.30f, 0.01f, 0.0f),
        new(MaterialId.Ice, "Ice", 0.92f, 0.03f, 0.04f, new Vector3(0.72f, 0.90f, 1.00f), true, 5.0f, 8, 0.00f, 0.04f, 0.0f, Melts: true),
        new(MaterialId.Plastic, "Plastic", 0.80f, 0.35f, 0.45f, new Vector3(0.95f, 0.55f, 0.22f), false, 8.0f, 8, 0.65f, 0.03f, 0.0f, Melts: true),
        new(MaterialId.Synthetic, "Synthetic", 1.20f, 0.62f, 0.12f, new Vector3(0.56f, 0.62f, 0.70f), true, 7.0f, 8, 0.35f, 0.75f, 0.0f, Melts: true),
        new(MaterialId.Explosive, "Explosive", 1.10f, 0.45f, 0.18f, new Vector3(0.90f, 0.16f, 0.10f), true, 2.5f, 10, 1.40f, 0.20f, 1.0f)
    ];

    public static MaterialDefinition Get(MaterialId id)
    {
        foreach (MaterialDefinition material in All)
        {
            if (material.Id == id)
            {
                return material;
            }
        }

        return All[0];
    }

    public static bool TryParse(string? text, out MaterialId id)
    {
        if (!string.IsNullOrWhiteSpace(text) && Enum.TryParse(text, ignoreCase: true, out id))
        {
            return true;
        }

        id = MaterialId.Custom;
        return false;
    }

    public static MaterialId GuessFromValues(RigidBody body)
    {
        MaterialId best = MaterialId.Custom;
        float bestScore = float.MaxValue;

        foreach (MaterialDefinition material in All)
        {
            if (material.Id == MaterialId.Custom) continue;

            float score = 0f;
            score += MathF.Abs(body.Density - material.Density) / 0.15f;
            score += MathF.Abs(body.Friction - material.Friction) / 0.20f;
            score += MathF.Abs(body.Restitution - material.Restitution) / 0.20f;
            score += MathF.Abs(body.Flammability - material.Flammability) / 0.25f;
            score += MathF.Abs(body.Conductivity - material.Conductivity) / 0.25f;

            if (body.Breakable != material.Breakable)
            {
                score += 2.5f;
            }

            score += MathF.Abs(body.BreakThreshold - material.BreakThreshold) / 4.0f;

            if (score < bestScore)
            {
                bestScore = score;
                best = material.Id;
            }
        }

        return bestScore <= 4.0f ? best : MaterialId.Custom;
    }
}
