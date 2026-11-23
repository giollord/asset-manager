using System.Numerics;

namespace AssetManager.Processing.Models;

/// <summary>
/// AssimpNetter exposes Assimp's matrices as <see cref="Matrix4x4"/> without converting them: the memory is Assimp's
/// row-major, column-vector layout (translation in M14/M24/M34). System.Numerics math expects row vectors
/// (translation in M41..M43), so every Assimp matrix is transposed before use, and back when building a scene.
/// </summary>
public static class AssimpMatrix
{
    /// <summary>An Assimp node/bone matrix in System.Numerics convention (use with <c>Vector3.Transform</c>, <c>child * parent</c>).</summary>
    public static Matrix4x4 ToNumerics(Matrix4x4 assimp) => Matrix4x4.Transpose(assimp);

    /// <summary>A System.Numerics matrix as Assimp expects it (for scenes built in code).</summary>
    public static Matrix4x4 FromNumerics(Matrix4x4 numerics) => Matrix4x4.Transpose(numerics);
}
