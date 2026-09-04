using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using Datamodel.Format;
using DMElement = Datamodel.Element;

namespace Tests.VMAP;

/// <summary>
/// Shared justification for helpers that must be methods: every public property of an element class is written to the file as an attribute.
/// </summary>
internal static class ValveMapSchema
{
    public const string SerializedPropertiesJustification = "Public properties of an element are serialized as attributes";
}

/// <summary>
///  Valve Map (VMAP) format version 29.
/// </summary>
/// <remarks>
/// Every class in this file maps one to one onto an element class of the file format, so a map can be loaded
/// through the ValveMapFile class of ValveResourceFormat, inspected and edited through these properties, and written back.
/// Attributes of a file that no property claims are kept on the element and written back unchanged.
/// </remarks>
[LowercaseProperties]
public class CMapRootElement : DMElement
{
    /// <summary>
    /// Whether this file is a prefab rather than a standalone map.
    /// </summary>
    public bool IsPrefab { get; set; }

    /// <summary>
    /// Hammer build number that wrote the file.
    /// </summary>
    public int EditorBuild { get; set; } = 8600;

    /// <summary>
    /// Map format version.
    /// </summary>
    public int EditorVersion { get; set; } = 400;

    /// <summary>
    /// Whether the 2D grid is drawn.
    /// </summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>
    /// Rotation snap in degrees.
    /// </summary>
    public int SnapRotationAngle { get; set; } = 15;

    /// <summary>
    /// Translation snap in world units.
    /// </summary>
    public float GridSpacing { get; set; } = 64;

    /// <summary>
    /// Whether the 3D grid is drawn.
    /// </summary>
    public bool Show3DGrid { get; set; } = true;

    /// <summary>
    /// Path to the item file this map uses, if any.
    /// </summary>
    [DMProperty(name: "itemFile")]
    public string ItemFile { get; set; } = string.Empty;

    /// <summary>
    /// Camera Hammer opens the map with.
    /// </summary>
    public CStoredCamera DefaultCamera { get; init; } = [];

    /// <summary>
    /// Saved cameras.
    /// </summary>
    [DMProperty(name: "3dcameras")]
    public CStoredCameras Cameras { get; init; } = [];

    /// <summary>
    /// Root of the map tree.
    /// </summary>
    public CMapWorld World { get; init; } = [];

    /// <summary>
    /// Per node hidden state. Hammer writes this attribute with the misspelled name.
    /// </summary>
    [DMProperty(name: "visbility")]
    public CVisibilityMgr Visibility { get; init; } = [];

    /// <summary>
    /// Map variables and their values.
    /// </summary>
    [DMProperty(name: "mapVariables")]
    public CMapVariableSet MapVariables { get; init; } = [];

    /// <summary>
    /// Root of the selection set tree.
    /// </summary>
    [DMProperty(name: "rootSelectionSet")]
    public CMapSelectionSet RootSelectionSet { get; init; } = [];

    /// <summary>
    /// Mesh snapshots the map references.
    /// </summary>
    [DMProperty(name: "m_ReferencedMeshSnapshots")]
    public Datamodel.ElementArray ReferencedMeshSnapshots { get; init; } = [];

    /// <summary>
    /// Whether the cordon is active.
    /// </summary>
    [DMProperty(name: "m_bIsCordoning")]
    public bool IsCordoning { get; set; }

    /// <summary>
    /// Whether cordon bounds are drawn.
    /// </summary>
    [DMProperty(name: "m_bCordonsVisible")]
    public bool CordonsVisible { get; set; }

    /// <summary>
    /// Per node instance data.
    /// </summary>
    [DMProperty(name: "nodeInstanceData")]
    public Datamodel.ElementArray NodeInstanceData { get; init; } = [];
}

/// <summary>
/// A saved 3D viewport camera.
/// </summary>
[LowercaseProperties]
public class CStoredCamera : DMElement
{
    /// <summary>
    /// Where the camera sits.
    /// </summary>
    public Vector3 Position { get; set; } = new Vector3(0, -1000, 1000);

    /// <summary>
    /// What the camera points at.
    /// </summary>
    public Vector3 LookAt { get; set; }
}

/// <summary>
/// The saved cameras of a map, and which one is active.
/// </summary>
[LowercaseProperties]
public class CStoredCameras : DMElement
{
    /// <summary>
    /// Index into <see cref="Cameras"/>, -1 when none is active.
    /// </summary>
    [DMProperty(name: "activecamera")]
    public int ActiveCameraIndex { get; set; } = -1;

    /// <summary>
    /// List of <see cref="CStoredCamera"/> elements.
    /// </summary>
    public Datamodel.ElementArray Cameras { get; init; } = [];
}

/// <summary>
/// Base of everything that appears in the map tree: a transform, an id, and child nodes.
/// </summary>
[CamelCaseProperties]
public abstract class MapNode : DMElement
{
    /// <summary>
    /// Position of the node, relative to its parent.
    /// </summary>
    public Vector3 Origin { get; set; }

    /// <summary>
    /// Rotation of the node, relative to its parent.
    /// </summary>
    public Datamodel.QAngle Angles { get; set; }

    /// <summary>
    /// Scale of the node, relative to its parent.
    /// </summary>
    public Vector3 Scales { get; set; } = new Vector3(1, 1, 1);

    /// <summary>
    /// Id of the node within the map, referenced by <see cref="CVisibilityMgr"/> and selection sets.
    /// </summary>
    public int NodeID { get; set; }

    /// <summary>
    /// Id the node keeps across prefab and instance boundaries.
    /// </summary>
    public ulong ReferenceID { get; set; }

    /// <summary>
    /// Child nodes parented to this one.
    /// </summary>
    public Datamodel.ElementArray Children { get; init; } = [];

    /// <summary>
    /// Whether the node is stripped at compile time.
    /// </summary>
    public bool EditorOnly { get; set; }

    /// <summary>
    /// Whether the node is hidden in Hammer.
    /// </summary>
    [DMProperty(name: "force_hidden")]
    public bool ForceHidden { get; set; }

    /// <summary>
    /// Whether Hammer refuses to move the node.
    /// </summary>
    public bool TransformLocked { get; set; }

    /// <summary>
    /// Entity keys driven by a map variable, parallel to <see cref="VariableNames"/>.
    /// </summary>
    public Datamodel.StringArray VariableTargetKeys { get; init; } = [];

    /// <summary>
    /// Map variables driving <see cref="VariableTargetKeys"/>.
    /// </summary>
    public Datamodel.StringArray VariableNames { get; init; } = [];

    /// <summary>
    /// Pins the node's transform to another node, a plain "DmElement" holding the properties of <see cref="CMapTransformPin"/>.
    /// </summary>
    public DMElement TransformPin { get; init; } = new CMapTransformPin();

    /// <summary>
    /// Name of the custom vis group the node belongs to, empty for none.
    /// </summary>
    public string CustomVisGroup { get; set; } = string.Empty;

    /// <summary>
    /// Seed Hammer uses for anything random about this node, such as smart prop evaluation.
    /// </summary>
    public int RandomSeed { get; set; }

    /// <summary>
    /// Enumerates the child nodes of the given type, in order.
    /// </summary>
    /// <typeparam name="T">Node class to filter by.</typeparam>
    public IEnumerable<T> GetChildren<T>() where T : MapNode
    {
        foreach (var child in Children)
        {
            if (child is T typed)
            {
                yield return typed;
            }
        }
    }
}

/// <summary>
/// References another map file and places its contents at this node.
/// </summary>
[CamelCaseProperties]
public class CMapPrefab : MapNode
{
    /// <summary>
    /// Output plugs of the prefab, one per entity IO connection.
    /// </summary>
    public DmePlugList RelayPlugData { get; init; } = [];

    /// <summary>
    /// List of <see cref="DmeConnectionData"/> elements, one per entity IO connection.
    /// </summary>
    public Datamodel.ElementArray ConnectionsData { get; init; } = [];

    /// <summary>
    /// The loaded contents of the prefab, null in a saved file.
    /// </summary>
    public DMElement? Target { get; init; }

    /// <summary>
    /// Map variables of the prefab this node overrides, parallel to <see cref="VariableOverrideValues"/>.
    /// </summary>
    public Datamodel.StringArray VariableOverrideNames { get; init; } = [];

    /// <summary>
    /// Values for <see cref="VariableOverrideNames"/>.
    /// </summary>
    public Datamodel.StringArray VariableOverrideValues { get; init; } = [];

    /// <summary>
    /// Path to the map file this prefab pulls in.
    /// </summary>
    public string TargetMapPath { get; set; } = string.Empty;

    /// <summary>
    /// Name given to the prefab instance.
    /// </summary>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>
    /// Whether entity names inside the prefab are prefixed to keep them unique.
    /// </summary>
    public bool FixupEntityNames { get; set; } = true;

    /// <summary>
    /// Whether <see cref="TargetName"/> is used as the prefix for entity names instead of a generated one.
    /// </summary>
    public bool UseTargetNameAsPrefix { get; set; }

    /// <summary>
    /// Whether the prefab still loads when it sits inside another prefab.
    /// </summary>
    public bool LoadIfNested { get; set; } = true;

    /// <summary>
    /// Whether the prefab becomes an entity at runtime.
    /// </summary>
    public bool PrefabRuntimeEntity { get; set; }

    /// <summary>
    /// Whether the prefab is spawned at runtime instead of merged at compile time.
    /// </summary>
    public bool LoadAtRuntime { get; set; }

    /// <summary>
    /// Tint applied to everything in the prefab.
    /// </summary>
    public Datamodel.Color TintColor { get; set; } = new Datamodel.Color(255, 255, 255, 255);

    /// <summary>
    /// Whether the prefab contents are left out of visibility computation.
    /// </summary>
    [DMProperty(name: "visexclude")]
    public bool VisExclude { get; set; }
}

/// <summary>
/// Base of every map node that carries entity key values and entity IO.
/// </summary>
[CamelCaseProperties]
public abstract class BaseEntity : MapNode
{
    /// <summary>
    /// Output plugs this entity fires through, one per entity IO connection.
    /// </summary>
    public DmePlugList RelayPlugData { get; init; } = [];

    /// <summary>
    /// List of <see cref="DmeConnectionData"/> elements, one per entity IO connection.
    /// </summary>
    public Datamodel.ElementArray ConnectionsData { get; init; } = [];

    /// <summary>
    /// The entity key values, including "classname".
    /// </summary>
    [DMProperty(name: "entity_properties")]
    public EditGameClassProps EntityProperties { get; init; } = [];

    /// <summary>
    /// The "classname" key value, or null when the entity has none.
    /// </summary>
    public string? GetEntityClassName() => EntityProperties.TryGetValue("classname", out var value) ? value as string : null;

    /// <summary>
    /// Sets one entity key value and returns this entity.
    /// </summary>
    /// <param name="name">Key to set.</param>
    /// <param name="value">Value to set it to.</param>
    public BaseEntity WithProperty(string name, string value)
    {
        EntityProperties[name] = value;
        return this;
    }

    /// <summary>
    /// Sets several entity key values and returns this entity.
    /// </summary>
    /// <param name="properties">Key value pairs to set.</param>
    public BaseEntity WithProperties(params (string name, string value)[] properties)
    {
        foreach (var (name, value) in properties)
        {
            EntityProperties[name] = value;
        }

        return this;
    }

    /// <summary>
    /// Sets the "classname" key value and returns this entity.
    /// </summary>
    /// <param name="className">Entity class name.</param>
    public BaseEntity WithClassName(string className)
        => WithProperty("classname", className);
}

/// <summary>
/// The output plugs of an entity, stored as parallel arrays.
/// </summary>
[CamelCaseProperties]
public class DmePlugList : DMElement
{
    /// <summary>
    /// Plug names.
    /// </summary>
    public Datamodel.StringArray Names { get; init; } = [];

    /// <summary>
    /// Data type of each plug.
    /// </summary>
    public Datamodel.IntArray DataTypes { get; init; } = [];

    /// <summary>
    /// Kind of each plug, input or output.
    /// </summary>
    public Datamodel.IntArray PlugTypes { get; init; } = [];

    /// <summary>
    /// Description of each plug.
    /// </summary>
    public Datamodel.StringArray Descriptions { get; init; } = [];
}

/// <summary>
/// One entity IO connection: an output firing an input on a target.
/// </summary>
[CamelCaseProperties]
public class DmeConnectionData : DMElement
{
    /// <summary>
    /// Output that fires, for example "OnTrigger".
    /// </summary>
    public string OutputName { get; set; } = string.Empty;

    /// <summary>
    /// How <see cref="TargetName"/> resolves to entities.
    /// </summary>
    public int TargetType { get; set; }

    /// <summary>
    /// Entities the output fires at.
    /// </summary>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>
    /// Input fired on the target, for example "Enable".
    /// </summary>
    public string InputName { get; set; } = string.Empty;

    /// <summary>
    /// Parameter passed to the input, overriding the output's own.
    /// </summary>
    public string OverrideParam { get; set; } = string.Empty;

    /// <summary>
    /// Delay before the input fires, in seconds.
    /// </summary>
    public float Delay { get; set; }

    /// <summary>
    /// How often the connection may fire, -1 for unlimited.
    /// </summary>
    public int TimesToFire { get; set; } = -1;
}

/// <summary>
///  A string->string dictionary. This stores entity KeyValues.
/// </summary>
public class EditGameClassProps : DMElement
{
}

/// <summary>
/// The world entity.
/// </summary>
[CamelCaseProperties]
public class CMapWorld : BaseEntity
{
    /// <summary>
    /// Next free decal id, handed out as decals are placed.
    /// </summary>
    public int NextDecalID { get; set; }

    /// <summary>
    /// Whether entity names are prefixed to keep them unique across prefabs.
    /// </summary>
    public bool FixupEntityNames { get; set; } = true;

    /// <summary>
    /// What the map is for, "standard" for a playable map.
    /// </summary>
    public string MapUsageType { get; set; } = "standard";

    /// <summary>
    /// Initializes a new instance of the <see cref="CMapWorld"/> class with classname "worldspawn".
    /// </summary>
    public CMapWorld()
    {
        EntityProperties["classname"] = "worldspawn";
    }
}

/// <summary>
/// Per node hidden state, as two parallel arrays.
/// </summary>
[CamelCaseProperties]
public class CVisibilityMgr : MapNode
{
    /// <summary>
    /// The nodes whose visibility is tracked.
    /// </summary>
    public Datamodel.ElementArray Nodes { get; init; } = [];

    /// <summary>
    /// Hidden flags, one per entry of <see cref="Nodes"/>. 0 is visible, 1 hidden through a selection set, higher values quick hidden.
    /// </summary>
    public Datamodel.IntArray HiddenFlags { get; init; } = [];

    /// <summary>
    /// Returns the hidden flags of a node, 0 when the node is visible or not tracked.
    /// </summary>
    /// <param name="node">Node to look up.</param>
    public int GetHiddenFlags(DMElement node)
    {
        var count = Math.Min(Nodes.Count, HiddenFlags.Count);

        for (var i = 0; i < count; i++)
        {
            if (Nodes[i]?.ID == node.ID)
            {
                return HiddenFlags[i];
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether a node is hidden in Hammer.
    /// </summary>
    /// <param name="node">Node to look up.</param>
    public bool IsHidden(DMElement node) => GetHiddenFlags(node) != 0;
}

/// <summary>
/// Map variables, stored as parallel arrays of name, value, type and type parameters.
/// </summary>
[CamelCaseProperties]
public class CMapVariableSet : DMElement
{
    /// <summary>
    /// Variable names.
    /// </summary>
    public Datamodel.StringArray VariableNames { get; init; } = [];

    /// <summary>
    /// Variable values.
    /// </summary>
    public Datamodel.StringArray VariableValues { get; init; } = [];

    /// <summary>
    /// Variable type names.
    /// </summary>
    public Datamodel.StringArray VariableTypeNames { get; init; } = [];

    /// <summary>
    /// Parameters of the variable types, such as the options of a choice.
    /// </summary>
    public Datamodel.StringArray VariableTypeParameters { get; init; } = [];

    /// <summary>
    /// Groups the choice variables are presented in.
    /// </summary>
    [DMProperty(name: "m_ChoiceGroups")]
    public Datamodel.ElementArray ChoiceGroups { get; init; } = [];

    /// <summary>
    /// Group each variable is shown under, parallel to <see cref="VariableNames"/>.
    /// </summary>
    public Datamodel.StringArray VariableGroupNames { get; init; } = [];

    /// <summary>
    /// Display order of the variables and choice groups.
    /// </summary>
    public Datamodel.IntArray VariableAndChoiceOrder { get; init; } = [];
}

/// <summary>
/// A group of map variables presented as one choice.
/// </summary>
public class CMapVariableChoiceGroup : DMElement
{
    /// <summary>
    /// Names of the variables the choice drives.
    /// </summary>
    [DMProperty(name: "m_ChoiceVariables")]
    public Datamodel.StringArray ChoiceVariables { get; init; } = [];

    /// <summary>
    /// The choices, each a plain element holding the values of <see cref="ChoiceVariables"/>.
    /// </summary>
    [DMProperty(name: "m_Choices")]
    public Datamodel.ElementArray Choices { get; init; } = [];

    /// <summary>
    /// Name of the active choice, empty for none.
    /// </summary>
    [DMProperty(name: "m_ActiveValue")]
    public string ActiveValue { get; set; } = string.Empty;

    /// <summary>
    /// Name shown for the group.
    /// </summary>
    [DMProperty(name: "m_GroupName")]
    public string GroupName { get; set; } = string.Empty;
}

/// <summary>
/// A named selection of map nodes, as shown in Hammer's selection set tree.
/// </summary>
[CamelCaseProperties]
public class CMapSelectionSet : DMElement
{
    /// <summary>
    /// Nested selection sets.
    /// </summary>
    public Datamodel.ElementArray Children { get; init; } = [];

    /// <summary>
    /// Name shown in Hammer.
    /// </summary>
    public string SelectionSetName { get; set; } = string.Empty;

    /// <summary>
    /// What this set selects: a <see cref="CObjectSelectionSetDataElement"/> for whole nodes, or a
    /// <see cref="CFaceSelectionSetDataElement"/>, <see cref="CEdgeSelectionSetDataElement"/> or <see cref="CVertexSelectionSetDataElement"/> for mesh components.
    /// </summary>
    public DMElement SelectionSetData { get; init; } = new CObjectSelectionSetDataElement();

    /// <summary>
    /// The selection data when this set selects whole nodes, otherwise null.
    /// </summary>
    [SuppressMessage("Design", "CA1024:Use properties where appropriate", Justification = ValveMapSchema.SerializedPropertiesJustification)]
    public CObjectSelectionSetDataElement? GetObjectSelection() => SelectionSetData as CObjectSelectionSetDataElement;

    /// <summary>
    /// The selection data when this set selects faces, otherwise null.
    /// </summary>
    [SuppressMessage("Design", "CA1024:Use properties where appropriate", Justification = ValveMapSchema.SerializedPropertiesJustification)]
    public CFaceSelectionSetDataElement? GetFaceSelection() => SelectionSetData as CFaceSelectionSetDataElement;

    /// <summary>
    /// The nodes this set selects.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this is a face selection set.</exception>
    public Datamodel.ElementArray GetSelectedObjects()
        => GetObjectSelection()?.SelectedObjects ?? throw new InvalidOperationException($"Selection set '{SelectionSetName}' does not select objects.");

    /// <summary>
    /// Enumerates this set and every set nested under it, depth first.
    /// </summary>
    public IEnumerable<CMapSelectionSet> EnumerateSelectionSets()
    {
        yield return this;

        foreach (var child in Children)
        {
            if (child is not CMapSelectionSet childSet)
            {
                continue;
            }

            foreach (var nested in childSet.EnumerateSelectionSets())
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CMapSelectionSet"/> class.
    /// </summary>
    public CMapSelectionSet() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="CMapSelectionSet"/> class with a name.
    /// </summary>
    /// <param name="name">Name shown in Hammer.</param>
    public CMapSelectionSet(string name)
    {
        SelectionSetName = name;
    }
}

/// <summary>
/// The map nodes a <see cref="CMapSelectionSet"/> selects.
/// </summary>
[CamelCaseProperties]
public class CObjectSelectionSetDataElement : DMElement
{
    /// <summary>
    /// The selected nodes.
    /// </summary>
    public Datamodel.ElementArray SelectedObjects { get; init; } = [];
}

/// <summary>
/// The mesh faces a <see cref="CMapSelectionSet"/> selects.
/// </summary>
[CamelCaseProperties]
public class CFaceSelectionSetDataElement : DMElement
{
    /// <summary>
    /// The <see cref="CMapMesh"/> nodes that own the selected faces.
    /// </summary>
    public Datamodel.ElementArray Meshes { get; init; } = [];

    /// <summary>
    /// Face indices into the meshes of <see cref="Meshes"/>.
    /// </summary>
    public Datamodel.IntArray Faces { get; init; } = [];
}

/// <summary>
/// The mesh edges a <see cref="CMapSelectionSet"/> selects.
/// </summary>
[CamelCaseProperties]
public class CEdgeSelectionSetDataElement : DMElement
{
    /// <summary>
    /// Half edge indices into the meshes of <see cref="Meshes"/>.
    /// </summary>
    public Datamodel.IntArray Edges { get; init; } = [];

    /// <summary>
    /// The <see cref="CMapMesh"/> nodes that own the selected edges.
    /// </summary>
    public Datamodel.ElementArray Meshes { get; init; } = [];
}

/// <summary>
/// The mesh vertices a <see cref="CMapSelectionSet"/> selects.
/// </summary>
[CamelCaseProperties]
public class CVertexSelectionSetDataElement : DMElement
{
    /// <summary>
    /// Vertex indices into the meshes of <see cref="Meshes"/>.
    /// </summary>
    public Datamodel.IntArray Vertices { get; init; } = [];

    /// <summary>
    /// The <see cref="CMapMesh"/> nodes that own the selected vertices.
    /// </summary>
    public Datamodel.ElementArray Meshes { get; init; } = [];
}

/// <summary>
/// A point or brush entity placed in the map.
/// </summary>
[CamelCaseProperties]
public class CMapEntity : BaseEntity
{
    /// <summary>
    /// Surface normal the entity was dropped onto when it was placed.
    /// </summary>
    public Vector3 HitNormal { get; set; }

    /// <summary>
    /// Whether the entity was generated by a tool rather than placed by hand.
    /// </summary>
    public bool IsProceduralEntity { get; set; }

    /// <summary>
    /// Returns the vertex paint data of a prop entity, or null when it has none.
    /// Hammer only writes the "extra_vertex_data" attribute on painted props, so it is not a class property.
    /// </summary>
    [SuppressMessage("Design", "CA1024:Use properties where appropriate", Justification = ValveMapSchema.SerializedPropertiesJustification)]
    public CDmExtraVertexData? GetExtraVertexData()
        => TryGetValue("extra_vertex_data", out var value) ? value as CDmExtraVertexData : null;
}

/// <summary>
/// Places another map group into the map with its own transform and tint.
/// </summary>
[CamelCaseProperties]
public class CMapInstance : MapNode
{
    /// <summary>
    /// Output plugs of the instance, one per entity IO connection.
    /// </summary>
    public DmePlugList RelayPlugData { get; init; } = [];

    /// <summary>
    /// List of <see cref="DmeConnectionData"/> elements, one per entity IO connection.
    /// </summary>
    public Datamodel.ElementArray ConnectionsData { get; init; } = [];

    /// <summary>
    /// A target <see cref="CMapGroup"/> to instance. With custom tint and transform.
    /// </summary>
    public DMElement? Target { get; init; }

    /// <summary>
    /// The instanced group, or null when <see cref="Target"/> is unset or not a group.
    /// </summary>
    [SuppressMessage("Design", "CA1024:Use properties where appropriate", Justification = ValveMapSchema.SerializedPropertiesJustification)]
    public CMapGroup? GetTargetGroup() => Target as CMapGroup;

    /// <summary>
    /// Tint applied to everything in the instance.
    /// </summary>
    public Datamodel.Color TintColor { get; set; } = new Datamodel.Color(255, 255, 255, 255);

    /// <summary>
    /// Whether the instance contents are left out of visibility computation.
    /// </summary>
    [DMProperty(name: "visexclude")]
    public bool VisExclude { get; set; }
}

/// <summary>
/// Groups child nodes under one selectable node. Also the target of a <see cref="CMapInstance"/>.
/// </summary>
[CamelCaseProperties]
public class CMapGroup : MapNode
{
    /// <summary>
    /// How the group deforms its children when it is scaled or sheared.
    /// </summary>
    public int DeformationMode { get; set; }
}

/// <summary>
/// A named world layer, which is a map group that compiles into its own layer.
/// </summary>
[CamelCaseProperties]
public class CMapWorldLayer : CMapGroup
{
    /// <summary>
    /// Name of the layer.
    /// </summary>
    public string WorldLayerName { get; set; } = string.Empty;
}

/// <summary>
/// A mesh authored in Hammer, with its render, lighting and physics settings.
/// </summary>
[CamelCaseProperties]
public class CMapMesh : MapNode
{
    /// <summary>
    /// Cubemap this mesh samples, empty to pick automatically.
    /// </summary>
    public string CubeMapName { get; set; } = string.Empty;

    /// <summary>
    /// Light group this mesh belongs to.
    /// </summary>
    public string LightGroup { get; set; } = string.Empty;

    /// <summary>
    /// Whether the mesh is left out of visibility computation.
    /// </summary>
    [DMProperty(name: "visexclude")]
    public bool VisExclude { get; set; }

    /// <summary>
    /// Whether the mesh renders in the dynamic pass.
    /// </summary>
    [DMProperty(name: "renderwithdynamic")]
    public bool RenderWithDynamic { get; set; }

    /// <summary>
    /// Whether height displacement is skipped for this mesh.
    /// </summary>
    public bool DisableHeightDisplacement { get; set; }

    /// <summary>
    /// Distance at which the mesh starts fading out, -1 to never fade.
    /// </summary>
    [DMProperty(name: "fademindist")]
    public float FadeMinDist { get; set; } = -1;

    /// <summary>
    /// Distance at which the mesh is fully faded out.
    /// </summary>
    [DMProperty(name: "fademaxdist")]
    public float FadeMaxDist { get; set; }

    /// <summary>
    /// Whether the mesh takes part in baked lighting.
    /// </summary>
    [DMProperty(name: "bakelighting")]
    public bool BakeLighting { get; set; } = true;

    /// <summary>
    /// Whether light probes are precomputed around the mesh.
    /// </summary>
    [DMProperty(name: "precomputelightprobes")]
    public bool PrecomputeLightProbes { get; set; } = true;

    /// <summary>
    /// Whether the mesh appears in cubemap renders.
    /// </summary>
    public bool RenderToCubemaps { get; set; } = true;

    /// <summary>
    /// Shadow casting mode, 0 to cast shadows.
    /// </summary>
    public int DisableShadows { get; set; }

    /// <summary>
    /// Angle below which adjacent faces are shaded smooth, in degrees.
    /// </summary>
    public float SmoothingAngle { get; set; } = 40f;

    /// <summary>
    /// Tint applied to the mesh.
    /// </summary>
    public Datamodel.Color TintColor { get; set; } = new Datamodel.Color(255, 255, 255, 255);

    /// <summary>
    /// Render alpha, 0 to 255.
    /// </summary>
    [DMProperty(name: "renderAmt")]
    public int RenderAmount { get; set; } = 255;

    /// <summary>
    /// Physics model to build for the mesh.
    /// </summary>
    public string PhysicsType { get; set; } = "default";

    /// <summary>
    /// Collision group of the mesh.
    /// </summary>
    public string PhysicsGroup { get; set; } = string.Empty;

    /// <summary>
    /// Collision categories the mesh counts as.
    /// </summary>
    public string PhysicsInteractsAs { get; set; } = string.Empty;

    /// <summary>
    /// Collision categories the mesh collides with.
    /// </summary>
    public string PhysicsInteractsWith { get; set; } = string.Empty;

    /// <summary>
    /// Collision categories the mesh never collides with.
    /// </summary>
    public string PhysicsInteractsExclude { get; set; } = string.Empty;

    /// <summary>
    /// The geometry itself.
    /// </summary>
    public CDmePolygonMesh MeshData { get; init; } = [];

    /// <summary>
    /// Whether the mesh occludes what is behind it.
    /// </summary>
    public bool UseAsOccluder { get; set; }

    /// <summary>
    /// Whether <see cref="PhysicsSimplificationError"/> overrides the default simplification.
    /// </summary>
    public bool PhysicsSimplificationOverride { get; set; }

    /// <summary>
    /// Error the physics simplification is allowed to introduce.
    /// </summary>
    public float PhysicsSimplificationError { get; set; }

    /// <summary>
    /// Whether emissive materials on the mesh light the scene when baking.
    /// </summary>
    public bool EmissiveLightingEnabled { get; set; } = true;

    /// <summary>
    /// Multiplier on the emissive light the mesh contributes when baking.
    /// </summary>
    public float EmissiveLightingBoost { get; set; } = 1f;

    /// <summary>
    /// Whether the mesh only exists to affect baked lighting and is not rendered.
    /// </summary>
    public bool LightingDummy { get; set; }

    /// <summary>
    /// Whether both sides of the mesh receive baked lighting.
    /// </summary>
    public bool BakeLightDoubleSided { get; set; }

    /// <summary>
    /// Whether the compiler must not merge this mesh with others.
    /// </summary>
    [DMProperty(name: "disablemerging")]
    public bool DisableMerging { get; set; }

    /// <summary>
    /// Whether the compiler keeps the vertices as authored instead of optimizing them.
    /// </summary>
    [DMProperty(name: "keep_vertices")]
    public bool KeepVertices { get; set; }

    /// <summary>
    /// Collision property overriding the one of the materials, empty for none.
    /// </summary>
    public string PhysicsCollisionProperty { get; set; } = string.Empty;

    /// <summary>
    /// Detail layers whose geometry is included in this mesh's physics.
    /// </summary>
    public Datamodel.ElementArray PhysicsIncludedDetailLayers { get; init; } = [];

    /// <summary>
    /// Detail layers whose geometry is left out of this mesh's physics.
    /// </summary>
    public Datamodel.ElementArray PhysicsMissingDetailLayers { get; init; } = [];
}

/// <summary>
/// A decal which uses its own hammer editable mesh to project onto geometry.
/// </summary>
[CamelCaseProperties]
public class CMapStaticOverlay : CMapMesh
{
    /// <summary>
    /// Node ids of the nodes the overlay projects onto.
    /// </summary>
    public Datamodel.IntArray ProjectionTargets { get; init; } = [];

    /// <summary>
    /// Order the overlay is drawn in where overlays stack, higher on top.
    /// </summary>
    public int RenderOrder { get; set; }

    /// <summary>
    /// Whether the overlay is left out at low quality settings.
    /// </summary>
    public bool DisabledInLowQuality { get; set; }

    /// <summary>
    /// Whether the overlay shades with the normals of the surface under it rather than its own.
    /// </summary>
    public bool UseBaseNormals { get; set; }

    /// <summary>
    /// How far from its mesh the overlay projects.
    /// </summary>
    public float ProjectionFar { get; set; } = 128f;

    /// <summary>
    /// Adjustments applied to the decal material, a plain "DmElement" holding the properties of <see cref="CMapOverlayMaterialAdjustmentParams"/>.
    /// </summary>
    [DMProperty(name: "MaterialAdjustmentParamsStruct")]
    public DMElement MaterialAdjustmentParamsStruct { get; init; } = new CMapOverlayMaterialAdjustmentParams();

    /// <summary>
    /// Whether the overlay also lands on faces turned away from it.
    /// </summary>
    public bool ProjectOnBackFaces { get; set; }

    /// <summary>
    /// Angle from the projection direction beyond which a face counts as facing away, in degrees.
    /// </summary>
    public float BackFacingAngle { get; set; } = 90f;

    /// <summary>
    /// What the overlay projects onto: everything (0), world geometry (1), models (2) or its
    /// <see cref="ProjectionTargets"/> (3).
    /// </summary>
    public int ProjectionMode { get; set; }
}

/// <summary>
/// The material adjustments of a <see cref="CMapStaticOverlay"/>.
/// </summary>
public class CMapOverlayMaterialAdjustmentParams : DMElement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CMapOverlayMaterialAdjustmentParams"/> class with Hammer's defaults.
    /// </summary>
    public CMapOverlayMaterialAdjustmentParams()
    {
        ClassName = "DmElement";
        Name = "MaterialAdjustmentParamsStruct";
    }

    /// <summary>Colour brightness adjustment, 0.5 for none.</summary>
    public float ColorBrightness { get; set; } = 0.5f;

    /// <summary>Colour contrast adjustment, 0.5 for none.</summary>
    public float ColorContrast { get; set; } = 0.5f;

    /// <summary>Opacity of the colour.</summary>
    public float ColorAlpha { get; set; } = 1f;

    /// <summary>Roughness brightness adjustment, 0.5 for none.</summary>
    public float RoughnessBrightness { get; set; } = 0.5f;

    /// <summary>Roughness contrast adjustment, 0.5 for none.</summary>
    public float RoughnessContrast { get; set; } = 0.5f;

    /// <summary>Opacity of the shading.</summary>
    public float ShadingAlpha { get; set; } = 1f;

    /// <summary>Strength of the decal's normal map.</summary>
    public float NormalIntensity { get; set; } = 0.75f;

    /// <summary>Whether the decal's roughness and metalness replace the surface's.</summary>
    public bool RoughnessMetalnessOverride { get; set; }

    /// <summary>Whether the decal's normals blend over the surface's.</summary>
    public bool NormalBlendOverride { get; set; } = true;
}

/// <summary>
/// Hammer's editable mesh, stored as a half edge mesh with parallel index arrays and data streams.
/// </summary>
[CamelCaseProperties]
public class CDmePolygonMesh : DMElement
{
    /// <summary>
    /// Index to one of the edges stemming from this vertex.
    /// </summary>
    public Datamodel.IntArray VertexEdgeIndices { get; init; } = [];

    /// <summary>
    /// Index to the <see cref="VertexData"/> streams.
    /// </summary>
    public Datamodel.IntArray VertexDataIndices { get; init; } = [];

    /// <summary>
    /// The destination vertex of this edge.
    /// </summary>
    public Datamodel.IntArray EdgeVertexIndices { get; init; } = [];

    /// <summary>
    /// Index to the opposite/twin edge.
    /// </summary>
    public Datamodel.IntArray EdgeOppositeIndices { get; init; } = [];

    /// <summary>
    /// Index to the next edge in the loop, in counter-clockwise order.
    /// </summary>
    public Datamodel.IntArray EdgeNextIndices { get; init; } = [];

    /// <summary>
    /// Per half-edge index to the adjacent face. -1 if void (open edge).
    /// </summary>
    public Datamodel.IntArray EdgeFaceIndices { get; init; } = [];

    /// <summary>
    /// Per half-edge index to the <see cref="EdgeData"/> streams.
    /// </summary>
    public Datamodel.IntArray EdgeDataIndices { get; init; } = [];

    /// <summary>
    /// Per half-edge index to the <see cref="FaceVertexData"/> streams.
    /// </summary>
    public Datamodel.IntArray EdgeVertexDataIndices { get; init; } = [];

    /// <summary>
    /// Per face index to one of the *inner* edges encapsulating this face.
    /// </summary>
    public Datamodel.IntArray FaceEdgeIndices { get; init; } = [];

    /// <summary>
    /// Per face index to the <see cref="FaceData"/> streams.
    /// </summary>
    public Datamodel.IntArray FaceDataIndices { get; init; } = [];

    /// <summary>
    /// List of material names. Indexed by the 'meshindex' <see cref="FaceData"/> stream.
    /// </summary>
    public Datamodel.StringArray Materials { get; init; } = [];

    /// <summary>
    /// Stores vertex positions.
    /// </summary>
    public CDmePolygonMeshDataArray VertexData { get; init; } = [];

    /// <summary>
    /// Stores vertex uv, normal, tangent, etc. Two per vertex (for each half?).
    /// </summary>
    public CDmePolygonMeshDataArray FaceVertexData { get; init; } = [];

    /// <summary>
    /// Stores edge data such as soft or hard normals.
    /// </summary>
    public CDmePolygonMeshDataArray EdgeData { get; init; } = [];

    /// <summary>
    /// Stores face data such as texture scale, UV offset, material, lightmap bias.
    /// </summary>
    public CDmePolygonMeshDataArray FaceData { get; init; } = [];

    /// <summary>
    /// Stores the subdivision level of each half-edge.
    /// </summary>
    public CDmePolygonMeshSubdivisionData SubdivisionData { get; init; } = [];

    /// <summary>
    /// Returns the number of faces in the mesh.
    /// </summary>
    [SuppressMessage("Design", "CA1024:Use properties where appropriate", Justification = ValveMapSchema.SerializedPropertiesJustification)]
    public int GetFaceCount() => FaceEdgeIndices.Count;

    /// <summary>
    /// Enumerates the half edges around a face, starting at its <see cref="FaceEdgeIndices"/> entry and following <see cref="EdgeNextIndices"/>.
    /// </summary>
    /// <param name="faceIndex">Index of the face.</param>
    public IEnumerable<int> GetFaceHalfEdges(int faceIndex)
    {
        var firstEdge = FaceEdgeIndices[faceIndex];
        var edge = firstEdge;

        do
        {
            yield return edge;
            edge = EdgeNextIndices[edge];
        }
        while (edge != firstEdge);
    }

    /// <summary>
    /// Enumerates the vertex indices around a face, in winding order. Index these into the <see cref="VertexData"/> streams through <see cref="VertexDataIndices"/>.
    /// </summary>
    /// <param name="faceIndex">Index of the face.</param>
    public IEnumerable<int> GetFaceVertices(int faceIndex)
    {
        foreach (var edge in GetFaceHalfEdges(faceIndex))
        {
            yield return EdgeVertexIndices[edge];
        }
    }
}

/// <summary>
/// A set of parallel data streams attached to one mesh component (vertices, half edges, or faces).
/// </summary>
[CamelCaseProperties]
public class CDmePolygonMeshDataArray : DMElement
{
    /// <summary>
    /// Number of entries each stream in <see cref="Streams"/> holds.
    /// </summary>
    public int Size { get; set; }

    /// <summary>
    /// Array of <see cref="CDmePolygonMeshDataStream"/>.
    /// </summary>
    public Datamodel.ElementArray Streams { get; init; } = [];

    /// <summary>
    /// Finds the stream with the given semantic name and index, for example "position" 0.
    /// </summary>
    /// <param name="semanticName">Semantic name of the stream.</param>
    /// <param name="semanticIndex">Channel of the semantic.</param>
    /// <returns>The stream, or null when there is none.</returns>
    public CDmePolygonMeshDataStream? GetStream(string semanticName, int semanticIndex = 0)
    {
        foreach (var element in Streams)
        {
            if (element is CDmePolygonMeshDataStream stream && stream.SemanticIndex == semanticIndex && stream.SemanticName == semanticName)
            {
                return stream;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the data of the stream with the given semantic name and index as a typed array, or null when there is no such stream or its data has another type.
    /// </summary>
    /// <typeparam name="T">Element type of the stream, int, Vector2, Vector3 or Vector4.</typeparam>
    /// <param name="semanticName">Semantic name of the stream.</param>
    /// <param name="semanticIndex">Channel of the semantic.</param>
    public Datamodel.Array<T>? GetStreamData<T>(string semanticName, int semanticIndex = 0)
        => GetStream(semanticName, semanticIndex)?.Data as Datamodel.Array<T>;
}

/// <summary>
/// Subdivision state of a <see cref="CDmePolygonMesh"/>.
/// </summary>
[CamelCaseProperties]
public class CDmePolygonMeshSubdivisionData : DMElement
{
    /// <summary>
    /// Subdivision level per half edge.
    /// </summary>
    public Datamodel.IntArray SubdivisionLevels { get; init; } = [];

    /// <summary>
    /// Array of <see cref="CDmePolygonMeshDataStream"/>.
    /// </summary>
    public Datamodel.ElementArray Streams { get; init; } = [];
}

/// <summary>
/// One named data stream of a <see cref="CDmePolygonMeshDataArray"/>, such as position, uv, or material index.
/// </summary>
[CamelCaseProperties]
public class CDmePolygonMeshDataStream : DMElement
{
    /// <summary>
    /// Name Hammer knows this stream by, for example "position" or "texcoord".
    /// </summary>
    public string StandardAttributeName { get; set; } = string.Empty;

    /// <summary>
    /// Name the stream binds to in the shader, for example "position" or "normal".
    /// </summary>
    public string SemanticName { get; set; } = string.Empty;

    /// <summary>
    /// Channel of <see cref="SemanticName"/> this stream fills.
    /// </summary>
    public int SemanticIndex { get; set; }

    /// <summary>
    /// Slot this stream occupies in the vertex buffer.
    /// </summary>
    public int VertexBufferLocation { get; set; }

    /// <summary>
    /// Flags describing how the stream is stored.
    /// </summary>
    public int DataStateFlags { get; set; }

    /// <summary>
    /// Subdivision stream this one mirrors, or null.
    /// </summary>
    public DMElement? SubdivisionBinding { get; init; }

    /// <summary>
    /// An int, vector2, vector3, or vector4 array: <see cref="Datamodel.IntArray"/>, <see cref="Datamodel.Vector2Array"/>,
    /// <see cref="Datamodel.Vector3Array"/> or <see cref="Datamodel.Vector4Array"/>.
    /// </summary>
    public IList? Data { get; init; }
}

/// <summary>
/// Pins a node's transform to another node, stored as a plain "DmElement" named "transformPin".
/// </summary>
[CamelCaseProperties]
public class CMapTransformPin : DMElement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CMapTransformPin"/> class with Hammer's defaults.
    /// </summary>
    public CMapTransformPin()
    {
        ClassName = "DmElement";
        Name = "transformPin";
    }

    /// <summary>
    /// Name of the node this one is pinned to, empty when not pinned.
    /// </summary>
    public string ReferenceName { get; set; } = string.Empty;

    /// <summary>
    /// Reference id of the node this one is pinned to, 0 when not pinned.
    /// </summary>
    public ulong TargetReferenceID { get; set; }

    /// <summary>
    /// Offset kept from the pinned node.
    /// </summary>
    public Vector3 OffsetOrigin { get; set; }

    /// <summary>
    /// Rotation kept relative to the pinned node.
    /// </summary>
    public Datamodel.QAngle OffsetAngles { get; set; }

    /// <summary>
    /// Whether the rotation follows the pinned node too.
    /// </summary>
    public bool PinAngles { get; set; } = true;

    /// <summary>
    /// Whether moving this node also moves the pinned node.
    /// </summary>
    public bool TwoWay { get; set; }
}

/// <summary>
/// A spline of <see cref="CMapPathNode"/> children, the base of cables and particle paths.
/// </summary>
[CamelCaseProperties]
public class CMapPath : CMapEntity
{
    /// <summary>
    /// How the spline interpolates between its nodes.
    /// </summary>
    public int InterpolationType { get; set; }

    /// <summary>
    /// Whether the last node connects back to the first.
    /// </summary>
    public bool ClosedLoop { get; set; }

    /// <summary>
    /// Distance between the points a particle snapshot samples along the path.
    /// </summary>
    public float ParticleSnapshotSpacing { get; set; } = 16f;
}

/// <summary>
/// One control point of a <see cref="CMapPath"/>.
/// </summary>
[CamelCaseProperties]
public class CMapPathNode : CMapEntity
{
    /// <summary>
    /// Tangent of the spline entering this node.
    /// </summary>
    public Vector3 InTangent { get; set; }

    /// <summary>
    /// Tangent of the spline leaving this node.
    /// </summary>
    public Vector3 OutTangent { get; set; }

    /// <summary>
    /// How <see cref="InTangent"/> is computed.
    /// </summary>
    public int InTangentType { get; set; } = 1;

    /// <summary>
    /// How <see cref="OutTangent"/> is computed.
    /// </summary>
    public int OutTangentType { get; set; } = 1;
}

/// <summary>
/// A cable rendered as a tube swept along a <see cref="CMapPath"/>.
/// </summary>
[CamelCaseProperties]
public class CMapCable : CMapPath
{
    /// <summary>
    /// Material of the cable.
    /// </summary>
    public string MaterialName { get; set; } = string.Empty;

    /// <summary>
    /// Tint applied to the cable.
    /// </summary>
    public Datamodel.Color TintColor { get; set; } = new Datamodel.Color(255, 255, 255, 255);

    /// <summary>
    /// Name of the entity the cable takes its lighting from, empty for none.
    /// </summary>
    public string LightingOriginName { get; set; } = string.Empty;

    /// <summary>
    /// Number of sides of the tube.
    /// </summary>
    public int NumSides { get; set; } = 4;

    /// <summary>
    /// Distance between the rings of the tube along the path.
    /// </summary>
    public float TessellationSpacing { get; set; } = 16f;

    /// <summary>
    /// Radius of the tube.
    /// </summary>
    public float Radius { get; set; } = 0.5f;

    /// <summary>
    /// Whether the tube faces inwards.
    /// </summary>
    public bool FlipFaces { get; set; }

    /// <summary>
    /// Whether the texture runs along the path (0) or around it (1).
    /// </summary>
    public int TextureOrientation { get; set; }

    /// <summary>
    /// Texture repeats per unit along the path.
    /// </summary>
    public float TextureScale { get; set; } = 0.25f;

    /// <summary>
    /// Texture repeats around the circumference.
    /// </summary>
    public float TextureRepeatsCircumference { get; set; } = 1f;

    /// <summary>
    /// Texture offset along the path.
    /// </summary>
    public float TextureOffsetAlongPath { get; set; }

    /// <summary>
    /// Texture offset around the circumference.
    /// </summary>
    public float TextureOffsetCircumference { get; set; }

    /// <summary>
    /// Whether the cable gets physics geometry.
    /// </summary>
    public bool CollisionEnabled { get; set; }

    /// <summary>
    /// Error the physics simplification is allowed to introduce.
    /// </summary>
    public float PhysicsSimplificationError { get; set; } = 2f;

    /// <summary>
    /// Whether the cable occludes what is behind it.
    /// </summary>
    public bool VisOccluder { get; set; }
}

/// <summary>
/// The cordon box, whose transform is the box: <see cref="MapNode.Origin"/> is its centre and <see cref="MapNode.Scales"/> its size.
/// </summary>
public class CMapCordon : MapNode
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CMapCordon"/> class named as Hammer does.
    /// </summary>
    public CMapCordon()
    {
        Name = "cordon";
    }
}

/// <summary>
/// Node holding the navigation mesh generation settings of the map.
/// </summary>
[CamelCaseProperties]
public class CMapNavData : MapNode
{
    /// <summary>
    /// The settings.
    /// </summary>
    public CDmeNavData NavData { get; init; } = [];
}

/// <summary>
/// Navigation mesh generation settings. Per agent hull values are parallel arrays with <see cref="SettingsAgentNumHulls"/> entries.
/// </summary>
[CamelCaseProperties]
public class CDmeNavData : DMElement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CDmeNavData"/> class named as Hammer does.
    /// </summary>
    public CDmeNavData()
    {
        Name = "navData";
    }

    /// <summary>Whether the project defaults override the settings stored here.</summary>
    public bool SettingsUseProjectDefaults { get; set; } = true;

    /// <summary>Size of a navigation tile, in units.</summary>
    public float SettingsTileSize { get; set; } = 128f;

    /// <summary>Size of a voxel cell, in units.</summary>
    public float SettingsCellSize { get; set; } = 1.5f;

    /// <summary>Height of a voxel cell, in units.</summary>
    public float SettingsCellHeight { get; set; } = 2f;

    /// <summary>Smallest region kept, in cells.</summary>
    public int SettingsRegionMinSize { get; set; } = 8;

    /// <summary>Regions smaller than this are merged, in cells.</summary>
    public int SettingsRegionMergeSize { get; set; } = 20;

    /// <summary>Sampling distance of the detail mesh.</summary>
    public float SettingsDetailSampleDist { get; set; } = 120f;

    /// <summary>Error the detail mesh is allowed to introduce.</summary>
    public float SettingsDetailSampleMaxError { get; set; } = 2f;

    /// <summary>Maximum vertices per navigation polygon.</summary>
    public int SettingsVertsPerPoly { get; set; } = 4;

    /// <summary>Longest polygon edge, in cells.</summary>
    public int SettingsEdgeMaxLen { get; set; } = 1200;

    /// <summary>Error an edge is allowed to deviate from the geometry.</summary>
    public float SettingsEdgeMaxError { get; set; } = 45f;

    /// <summary>Areas on edges smaller than this are removed, -1 to keep them.</summary>
    public float SettingsSmallAreaOnEdgeRemovalSize { get; set; } = -1f;

    /// <summary>Name of the agent hull preset, empty for none.</summary>
    public string SettingsAgentHullPreset { get; set; } = string.Empty;

    /// <summary>Path of the vdata overriding the agent hulls, empty for none.</summary>
    public string SettingsAgentHullsVDataOverride { get; set; } = string.Empty;

    /// <summary>Number of agent hulls.</summary>
    public int SettingsAgentNumHulls { get; set; } = 1;

    /// <summary>Whether each hull is generated.</summary>
    public Datamodel.BoolArray SettingsAgentEnabled { get; init; } = [true];

    /// <summary>Radius of each hull.</summary>
    public Datamodel.FloatArray SettingsAgentRadius { get; init; } = [15f];

    /// <summary>Height of each hull.</summary>
    public Datamodel.FloatArray SettingsAgentHeight { get; init; } = [71f];

    /// <summary>Whether each hull has a crouching height.</summary>
    public Datamodel.BoolArray SettingsAgentShortHeightEnabled { get; init; } = [false];

    /// <summary>Crouching height of each hull.</summary>
    public Datamodel.FloatArray SettingsAgentShortHeight { get; init; } = [35.5f];

    /// <summary>Whether each hull has a crawling height.</summary>
    public Datamodel.BoolArray SettingsAgentCrawlEnabled { get; init; } = [false];

    /// <summary>Crawling height of each hull.</summary>
    public Datamodel.FloatArray SettingsAgentCrawlHeight { get; init; } = [17.5f];

    /// <summary>Highest step each hull can climb.</summary>
    public Datamodel.FloatArray SettingsAgentMaxClimb { get; init; } = [17.5f];

    /// <summary>Steepest slope each hull can walk, in degrees.</summary>
    public Datamodel.IntArray SettingsAgentMaxSlope { get; init; } = [50];

    /// <summary>Furthest each hull can jump down.</summary>
    public Datamodel.FloatArray SettingsAgentMaxJumpDownDist { get; init; } = [240f];

    /// <summary>Furthest each hull can jump horizontally.</summary>
    public Datamodel.FloatArray SettingsAgentMaxJumpHorizDistBase { get; init; } = [64f];

    /// <summary>Highest each hull can jump up.</summary>
    public Datamodel.FloatArray SettingsAgentMaxJumpUpDist { get; init; } = [0f];

    /// <summary>Cells eroded from the border for each hull, -1 for the default.</summary>
    public Datamodel.IntArray SettingsAgentBorderErosion { get; init; } = [-1];
}

/// <summary>
/// A smart prop placed in the map, evaluated by Hammer into the props it stands for.
/// </summary>
[CamelCaseProperties]
public class CMapSmartProp : MapNode
{
    /// <summary>
    /// Nodes the smart prop shapes itself around, each wrapped in a plain element with a "value" attribute.
    /// </summary>
    public Datamodel.ElementArray ShapeReferences { get; init; } = [];

    /// <summary>
    /// Path of the smart prop definition.
    /// </summary>
    public string SmartPropFilename { get; set; } = string.Empty;

    /// <summary>
    /// Tint applied to the evaluated props.
    /// </summary>
    public Datamodel.Color TintColor { get; set; } = new Datamodel.Color(255, 255, 255, 255);

    /// <summary>
    /// Whether Hammer keeps the current evaluation instead of re-evaluating on changes.
    /// </summary>
    public bool EvaluationLocked { get; set; }

    /// <summary>
    /// Whether the evaluation is constrained to the prefab the smart prop sits in.
    /// </summary>
    public bool ConstrainToPrefab { get; set; }

    /// <summary>
    /// Render alpha, 0 to 255.
    /// </summary>
    public int Alpha { get; set; } = 255;

    /// <summary>
    /// Distance beyond which the props are culled, 0 for never.
    /// </summary>
    public float CullDistance { get; set; }

    /// <summary>
    /// Distance at which the props start fading out, -1 to never fade.
    /// </summary>
    public float FadeStartDistance { get; set; } = -1f;

    /// <summary>
    /// Name of the entity the props take their lighting from, empty for none.
    /// </summary>
    public string LightingOriginName { get; set; } = string.Empty;

    /// <summary>
    /// Shadow casting mode, 0 to cast shadows.
    /// </summary>
    public int DisableShadows { get; set; }

    /// <summary>
    /// How the props take part in baked lighting, -1 for the default. Hammer stores this attribute with the misspelled name.
    /// </summary>
    [DMProperty(name: "bakedLigthtingMode")]
    public int BakedLightingMode { get; set; } = -1;

    /// <summary>
    /// Lightmap resolution bias of the props.
    /// </summary>
    public int LightmapScaleBias { get; set; }

    /// <summary>
    /// Whether both sides of the props receive baked lighting.
    /// </summary>
    public bool BakeLightingDoubleSided { get; set; }

    /// <summary>
    /// Whether emissive materials on the props light the scene when baking.
    /// </summary>
    public bool EmissiveLightingEnabled { get; set; } = true;

    /// <summary>
    /// Multiplier on the emissive light the props contribute when baking.
    /// </summary>
    public float EmissiveLightingBoost { get; set; } = 1f;

    /// <summary>
    /// Collision mode of the props, -1 for the default.
    /// </summary>
    public int CollisionMode { get; set; } = -1;

    /// <summary>
    /// Collision property overriding the one of the props' materials, empty for none.
    /// </summary>
    public string CollisionPropertyOverride { get; set; } = string.Empty;

    /// <summary>
    /// Whether the props occlude what is behind them.
    /// </summary>
    public bool IsVisOccluder { get; set; }

    /// <summary>
    /// Whether the props appear in cubemap renders.
    /// </summary>
    public bool RenderToCubeMaps { get; set; } = true;

    /// <summary>
    /// Whether the props are left out at low quality settings.
    /// </summary>
    public bool DisabledInLowQuality { get; set; }

    /// <summary>
    /// Whether the props are baked into the world geometry.
    /// </summary>
    public bool BakeToWorld { get; set; }

    /// <summary>
    /// Whether the compiler must not merge the props with others.
    /// </summary>
    public bool DisableMerging { get; set; }

    /// <summary>
    /// Whether the props render in the dynamic pass.
    /// </summary>
    public bool RenderWithDynamic { get; set; }

    /// <summary>
    /// The evaluated state of the smart prop, a plain "DmElement" named "nodeData" holding its parameters and configuration.
    /// </summary>
    public DMElement NodeData { get; init; } = new DMElement { ClassName = "DmElement", Name = "nodeData" };
}

/// <summary>
/// Baked per vertex lighting of one node, stored in the root's <see cref="CMapRootElement.NodeInstanceData"/> and named after the node id.
/// </summary>
[CamelCaseProperties]
public class CDmeNodeInstanceData : DMElement
{
    /// <summary>
    /// Baked light colour per vertex.
    /// </summary>
    public Datamodel.ColorArray VertexLightingData { get; init; } = [];

    /// <summary>
    /// Position of each baked vertex.
    /// </summary>
    public Datamodel.Vector3Array VertexLightingPositions { get; init; } = [];

    /// <summary>
    /// Normal of each baked vertex.
    /// </summary>
    public Datamodel.Vector3Array VertexLightingNormals { get; init; } = [];
}

/// <summary>
/// The render geometry of a model the map references, kept so that vertex paint can be applied to it.
/// </summary>
public class CDmeReferencedMeshSnapshot : DMElement
{
    /// <summary>
    /// Path of the model.
    /// </summary>
    [DMProperty(name: "m_MeshResourceName")]
    public string MeshResourceName { get; set; } = string.Empty;

    /// <summary>
    /// List of <see cref="CDmeDrawCallSnapshot"/> elements, one per draw call of the model.
    /// </summary>
    [DMProperty(name: "m_DrawCalls")]
    public Datamodel.ElementArray DrawCalls { get; init; } = [];
}

/// <summary>
/// The vertices of one draw call of a <see cref="CDmeReferencedMeshSnapshot"/>.
/// </summary>
public class CDmeDrawCallSnapshot : DMElement
{
    /// <summary>
    /// Vertex positions.
    /// </summary>
    [DMProperty(name: "m_Positions")]
    public Datamodel.Vector3Array Positions { get; init; } = [];

    /// <summary>
    /// Vertex normals.
    /// </summary>
    [DMProperty(name: "m_Normals")]
    public Datamodel.Vector3Array Normals { get; init; } = [];

    /// <summary>
    /// Vertex texture coordinates.
    /// </summary>
    [DMProperty(name: "m_Texcoords")]
    public Datamodel.Vector2Array Texcoords { get; init; } = [];

    /// <summary>
    /// Hash of the draw call, used to match it to the compiled model.
    /// </summary>
    [DMProperty(name: "m_nHash")]
    public int Hash { get; set; }

    /// <summary>
    /// Material of the draw call.
    /// </summary>
    [DMProperty(name: "m_Material")]
    public string Material { get; set; } = string.Empty;
}

/// <summary>
/// Binds a <see cref="CDmePolygonMeshDataStream"/> to the subdivision data that drives it.
/// </summary>
[CamelCaseProperties]
public class CDmePolygonMeshSubdivisiondataBinding : DMElement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CDmePolygonMeshSubdivisiondataBinding"/> class named as Hammer does.
    /// </summary>
    public CDmePolygonMeshSubdivisiondataBinding()
    {
        Name = "subdivisionBinding";
    }

    /// <summary>
    /// Mesh component the target stream belongs to, -1 for none.
    /// </summary>
    public int TargetDataType { get; set; } = -1;

    /// <summary>
    /// Index of the target stream within its component, -1 for none.
    /// </summary>
    public int TargetStreamIndex { get; set; } = -1;

    /// <summary>
    /// Where the subdivided values come from.
    /// </summary>
    public int StreamSourceType { get; set; }
}

/// <summary>
/// Vertex paint applied to a prop entity, stored on the entity as "extra_vertex_data".
/// </summary>
public class CDmExtraVertexData : DMElement
{
    /// <summary>
    /// List of <see cref="CDmExtraVertexStream"/> elements, one per painted draw call.
    /// </summary>
    [DMProperty(name: "m_ExtraStreams")]
    public Datamodel.ElementArray ExtraStreams { get; init; } = [];
}

/// <summary>
/// Vertex paint of one draw call of a prop.
/// </summary>
public class CDmExtraVertexStream : DMElement
{
    /// <summary>
    /// Index of the draw call within the mesh.
    /// </summary>
    [DMProperty(name: "m_nDrawCallIndex")]
    public int DrawCallIndex { get; set; }

    /// <summary>
    /// Index of the mesh within the model.
    /// </summary>
    [DMProperty(name: "m_nMeshIndex")]
    public int MeshIndex { get; set; }

    /// <summary>
    /// A "DmeVertexData" element holding the painted streams, such as "VertexPaintTintColor" and "PerVertexLighting", each with an "Indices" array.
    /// </summary>
    [DMProperty(name: "m_pVertexData")]
    public DMElement? VertexData { get; init; }
}
