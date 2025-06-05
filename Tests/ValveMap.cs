using Datamodel.Format;
using System.Numerics;
using DMElement = Datamodel.Element;

namespace Tests.VMAP;

#nullable enable

/// <summary>
///  Valve Map (VMAP) format version 29.
/// </summary>
internal class CMapRootElement : DMElement
{
    public bool isprefab { get; set; }
    public int editorbuild { get; set; } = 8600;
    public int editorversion { get; set; } = 400;
    public bool showgrid { get; set; } = true;
    public int snaprotationangle { get; set; } = 15;
    public float gridspacing { get; set; } = 64;
    public bool show3dgrid { get; set; } = true;
    [DMProperty(name: "itemFile")]
    public string Itemfile { get; set; } = string.Empty;
    public CStoredCamera defaultcamera { get; set; } = [];
    [DMProperty(name: "3dcameras")]
    public CStoredCameras Cameras { get; set; } = [];
    public CMapWorld world { get; set; } = [];
    [DMProperty(name: "visbility")]
    public CVisibilityMgr Visibility { get; set; } = [];
    [DMProperty(name: "mapVariables")]
    public CMapVariableSet MapVariables { get; set; } = [];
    [DMProperty(name: "rootSelectionSet")]
    public CMapSelectionSet RootSelectionSet { get; set; } = [];
    [DMProperty(name: "m_ReferencedMeshSnapshots")]
    public Datamodel.ElementArray ReferencedMeshSnapshots { get; set; } = [];
    [DMProperty(name: "m_bIsCordoning")]
    public bool IsCordoning { get; set; }
    [DMProperty(name: "m_bCordonsVisible")]
    public bool CordonsVisible { get; set; }
    [DMProperty(name: "nodeInstanceData")]
    public Datamodel.ElementArray NodeInstanceData { get; set; } = [];
}


internal class CStoredCamera : DMElement
{
    public Vector3 position { get; set; } = new Vector3(0, -1000, 1000);
    public Vector3 lookat { get; set; }
}


internal class CStoredCameras : DMElement
{
    [DMProperty(name: "activecamera")]
    public int ActiveCameraIndex { get; set; } = -1;
    public Datamodel.ElementArray cameras { get; set; } = [];
}


internal abstract class MapNode : DMElement
{
    public Vector3 origin { get; set; }
    public Datamodel.QAngle angles { get; set; }
    public Vector3 scales { get; set; } = new Vector3(1, 1, 1);

    public int nodeID { get; set; }
    public ulong referenceID { get; set; }

    public Datamodel.ElementArray children { get; set; } = [];

    public bool editorOnly { get; set; }
    [DMProperty(name: "force_hidden")]
    public bool ForceHidden { get; set; }
    public bool transformLocked { get; set; }
    public Datamodel.StringArray variableTargetKeys { get; set; } = [];
    public Datamodel.StringArray variableNames { get; set; } = [];
}

internal class CMapPrefab : MapNode
{
    public bool fixupEntityNames { get; set; } = true;
    public bool loadAtRuntime { get; set; }
    public bool loadIfNested { get; set; } = true;
    public string targetMapPath { get; set; } = string.Empty;
    public string targetName { get; set; } = string.Empty;
}


internal abstract class BaseEntity : MapNode
{
    public DmePlugList relayPlugData { get; set; } = [];
    public Datamodel.ElementArray connectionsData { get; set; } = [];
    [DMProperty(name: "entity_properties")]
    public EditGameClassProps EntityProperties { get; set; } = [];

    public BaseEntity WithProperty(string name, string value)
    {
        EntityProperties[name] = value;
        return this;
    }

    public BaseEntity WithProperties(params (string name, string value)[] properties)
    {
        foreach (var (name, value) in properties)
        {
            EntityProperties[name] = value;
        }

        return this;
    }

    public BaseEntity WithClassName(string className)
        => WithProperty("classname", className);
}


internal class DmePlugList : DMElement
{
    public Datamodel.StringArray names { get; set; } = [];
    public Datamodel.IntArray dataTypes { get; set; } = [];
    public Datamodel.IntArray plugTypes { get; set; } = [];
    public Datamodel.StringArray descriptions { get; set; } = [];
}


internal class DmeConnectionData : DMElement
{
    public string outputName { get; set; } = string.Empty;
    public int targetType { get; set; }
    public string targetName { get; set; } = string.Empty;
    public string inputName { get; set; } = string.Empty;
    public string overrideParam { get; set; } = string.Empty;
    public float delay { get; set; }
    public int timesToFire { get; set; } = -1;
}

/// <summary>
///  A string->string dictionary. This stores entity KeyValues.
/// </summary>
internal class EditGameClassProps : DMElement
{
}

/// <summary>
/// The world entity.
/// </summary>

internal class CMapWorld : BaseEntity
{
    public int nextDecalID { get; set; }
    public bool fixupEntityNames { get; set; } = true;
    public string mapUsageType { get; set; } = "standard";

    public CMapWorld()
    {
        EntityProperties["classname"] = "worldspawn";
    }
}


internal class CVisibilityMgr : MapNode
{
    public Datamodel.ElementArray nodes { get; set; } = [];
    public Datamodel.IntArray hiddenFlags { get; set; } = [];
}


internal class CMapVariableSet : DMElement
{
    public Datamodel.StringArray variableNames { get; set; } = [];
    public Datamodel.StringArray variableValues { get; set; } = [];
    public Datamodel.StringArray variableTypeNames { get; set; } = [];
    public Datamodel.StringArray variableTypeParameters { get; set; } = [];
    [DMProperty(name: "m_ChoiceGroups")]
    public Datamodel.ElementArray ChoiceGroups { get; set; } = [];
}


[CamelCaseProperties]
internal class CMapSelectionSet : DMElement
{
    public Datamodel.ElementArray Children { get; } = [];
    public string SelectionSetName { get; set; } = string.Empty;
    public DMElement SelectionSetData { get; set; } = [];

    public CMapSelectionSet() { }
    public CMapSelectionSet(string name)
    {
        SelectionSetName = name;
    }
}


internal class CObjectSelectionSetDataElement : DMElement
{
    public Datamodel.ElementArray selectedObjects { get; set; } = [];
}

internal class CFaceSelectionSetDataElement : DMElement
{
    public Datamodel.IntArray faces { get; set; } = [];
    public Datamodel.ElementArray meshes { get; set; } = [];
}


internal class CMapEntity : BaseEntity
{
    public Vector3 hitNormal { get; set; }
    public bool isProceduralEntity { get; set; }
}


[LowercaseProperties]
internal class CMapInstance : BaseEntity
{
    /// <summary>
    /// A target <see cref="CMapGroup"/> to instance. With custom tint and transform.
    /// </summary>
    public CMapGroup? Target { get; set; }
    public Datamodel.Color TintColor { get; set; } = new Datamodel.Color(255, 255, 255, 255);
}

internal class CMapGroup : MapNode
{
}


internal class CMapWorldLayer : CMapGroup
{
    public string worldLayerName { get; set; } = string.Empty;
}


internal class CMapMesh : MapNode
{
    public string cubeMapName { get; set; } = string.Empty;
    public string lightGroup { get; set; } = string.Empty;
    [DMProperty(name: "visexclude")]
    public bool VisExclude { get; set; }
    [DMProperty(name: "renderwithdynamic")]
    public bool RenderWithDynamic { get; set; }
    public bool disableHeightDisplacement { get; set; }
    [DMProperty(name: "fademindist")]
    public float FadeMinDist { get; set; } = -1;
    [DMProperty(name: "fademaxdist")]
    public float FadeMaxDist { get; set; }
    [DMProperty(name: "bakelighting")]
    public bool BakeLighting { get; set; } = true;
    [DMProperty(name: "precomputelightprobes")]
    public bool PrecomputeLightProbes { get; set; } = true;
    public bool renderToCubemaps { get; set; } = true;
    public int disableShadows { get; set; }
    public float smoothingAngle { get; set; } = 40f;
    public Datamodel.Color tintColor { get; set; } = new Datamodel.Color(255, 255, 255, 255);
    [DMProperty(name: "renderAmt")]
    public int RenderAmount { get; set; } = 255;
    public string physicsType { get; set; } = "default";
    public string physicsGroup { get; set; } = string.Empty;
    public string physicsInteractsAs { get; set; } = string.Empty;
    public string physicsInteractWsith { get; set; } = string.Empty;
    public string physicsInteractsExclude { get; set; } = string.Empty;
    public CDmePolygonMesh meshData { get; set; } = [];
    public bool useAsOccluder { get; set; }
    public bool physicsSimplificationOverride { get; set; }
    public float physicsSimplificationError { get; set; }
}


internal class CDmePolygonMesh : MapNode
{
    /// <summary>
    /// Index to one of the edges stemming from this vertex.
    /// </summary>
    public Datamodel.IntArray vertexEdgeIndices { get; set; } = [];

    /// <summary>
    /// Index to the <see cref="VertexData"/> streams.
    /// </summary>
    public Datamodel.IntArray vertexDataIndices { get; set; } = [];

    /// <summary>
    /// The destination vertex of this edge.
    /// </summary>
    public Datamodel.IntArray edgeVertexIndices { get; set; } = [];

    /// <summary>
    /// Index to the opposite/twin edge.
    /// </summary>
    public Datamodel.IntArray edgeOppositeIndices { get; set; } = [];

    /// <summary>
    /// Index to the next edge in the loop, in counter-clockwise order.
    /// </summary>
    public Datamodel.IntArray edgeNextIndices { get; set; } = [];

    /// <summary>
    /// Per half-edge index to the adjacent face. -1 if void (open edge).
    /// </summary>
    public Datamodel.IntArray edgeFaceIndices { get; set; } = [];

    /// <summary>
    /// Per half-edge index to the <see cref="EdgeData"/> streams.
    /// </summary>
    public Datamodel.IntArray edgeDataIndices { get; set; } = [];

    /// <summary>
    /// Per half-edge index to the <see cref="FaceVertexData"/> streams.
    /// </summary>
    public Datamodel.IntArray edgeVertexDataIndices { get; set; } = [];

    /// <summary>
    /// Per face index to one of the *inner* edges encapsulating this face.
    /// </summary>
    public Datamodel.IntArray faceEdgeIndices { get; set; } = [];

    /// <summary>
    /// Per face index to the <see cref="FaceData"/> streams.
    /// </summary>
    public Datamodel.IntArray faceDataIndices { get; set; } = [];

    /// <summary>
    /// List of material names. Indexed by the 'meshindex' <see cref="FaceData"/> stream.
    /// </summary>
    public Datamodel.StringArray materials { get; set; } = [];

    /// <summary>
    /// Stores vertex positions.
    /// </summary>
    public CDmePolygonMeshDataArray vertexData { get; set; } = [];

    /// <summary>
    /// Stores vertex uv, normal, tangent, etc. Two per vertex (for each half?).
    /// </summary>
    public CDmePolygonMeshDataArray faceVertexData { get; set; } = [];

    /// <summary>
    /// Stores edge data such as soft or hard normals.
    /// </summary>
    public CDmePolygonMeshDataArray edgeData { get; set; } = [];

    /// <summary>
    /// Stores face data such as texture scale, UV offset, material, lightmap bias.
    /// </summary>
    public CDmePolygonMeshDataArray faceData { get; set; } = [];

    public CDmePolygonMeshSubdivisionData subdivisionData { get; set; } = [];
}


internal class CDmePolygonMeshDataArray : DMElement
{
    public int size { get; set; }
    /// <summary>
    /// Array of <see cref="CDmePolygonMeshDataStream"/>.
    /// </summary>
    public Datamodel.ElementArray streams { get; set; } = [];
}


internal class CDmePolygonMeshSubdivisionData : DMElement
{
    public Datamodel.IntArray subdivisionLevels { get; set; } = [];
    /// <summary>
    /// Array of <see cref="CDmePolygonMeshDataStream"/>.
    /// </summary>
    public Datamodel.ElementArray streams { get; set; } = [];
}

internal class CDmePolygonMeshDataStream : DMElement
{
    public string standardAttributeName { get; set; } = string.Empty;
    public string semanticName { get; set; } = string.Empty;
    public int semanticIndex { get; set; }
    public int vertexBufferLocation { get; set; }
    public int dataStateFlags { get; set; }
    public DMElement? subdivisionBinding { get; set; }
    /// <summary>
    /// An int, vector2, vector3, or vector4 array.
    /// </summary>
    public System.Collections.IList? data { get; set; }
}

/// <remarks>
/// Note: The deserializer does not support generic types, but the serializer does.
/// </remarks>
/// <typeparam name="T">Int, Vector2, Vector3, or Vector4</typeparam>
internal class CDmePolygonMeshDataStream<T> : CDmePolygonMeshDataStream
{
    public new required Datamodel.Array<T> data { get; set; }
}
