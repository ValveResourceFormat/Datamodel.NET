using System.Numerics;

namespace Datamodel;

public record struct QAngle(float Pitch, float Yaw, float Roll)
{
    public static implicit operator Vector3(QAngle q) => new(q.Pitch, q.Yaw, q.Roll);
    public static implicit operator QAngle(Vector3 v) => new(v.X, v.Y, v.Z);
}
