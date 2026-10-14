using Opc.Ua;
using Opc.Ua.Server;

namespace OpcUaExporter.SimServer;

/// <summary>
/// Address space of the simulation server, under Objects/Simulation (namespace
/// <see cref="NamespaceUri"/>, string node ids):
/// <list type="bullet">
/// <item><c>Dynamic/*</c> — read-only values that change once a second.</item>
/// <item><c>Static/*</c> — writable values of the common built-in types (DateTime included).</item>
/// <item><c>Deep/Level1/.../Level5</c> — a nested branch for exercising the tree browser.</item>
/// </list>
/// </summary>
internal sealed class SimNodeManager : CustomNodeManager2
{
    public const string NamespaceUri = "urn:OpcUaExporter:SimServer:Simulation";

    private readonly List<(BaseDataVariableState Variable, Func<long, object> Next)> _dynamic = [];
    private Timer? _timer;
    private long _tick;

    public SimNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, NamespaceUri)
    {
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();

            var root = CreateFolder(null, "Simulation");
            root.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root.NodeId));
            root.EventNotifier = EventNotifiers.SubscribeToEvents;
            AddRootNotifier(root);

            var dynamicFolder = CreateFolder(root, "Dynamic");
            AddDynamic(dynamicFolder, "Counter", DataTypeIds.Int32, t => (int)t);
            AddDynamic(dynamicFolder, "Sine", DataTypeIds.Double, t => Math.Round(Math.Sin(t * Math.PI / 30) * 100, 3));
            AddDynamic(dynamicFolder, "Sawtooth", DataTypeIds.Float, t => (float)(t % 20));
            AddDynamic(dynamicFolder, "Square", DataTypeIds.Boolean, t => t / 5 % 2 == 0);
            AddDynamic(dynamicFolder, "Random", DataTypeIds.Double, _ => Math.Round(Random.Shared.NextDouble() * 50, 3));
            AddDynamic(dynamicFolder, "Clock", DataTypeIds.DateTime, _ => DateTime.UtcNow);
            AddDynamic(dynamicFolder, "Text", DataTypeIds.String, t => $"Tick {t}");

            var staticFolder = CreateFolder(root, "Static");
            CreateVariable(staticFolder, "Boolean", DataTypeIds.Boolean, false, writable: true);
            CreateVariable(staticFolder, "Byte", DataTypeIds.Byte, (byte)7, writable: true);
            CreateVariable(staticFolder, "Int16", DataTypeIds.Int16, (short)-16, writable: true);
            CreateVariable(staticFolder, "Int32", DataTypeIds.Int32, 42, writable: true);
            CreateVariable(staticFolder, "UInt32", DataTypeIds.UInt32, 42u, writable: true);
            CreateVariable(staticFolder, "Int64", DataTypeIds.Int64, 4_200_000_000L, writable: true);
            CreateVariable(staticFolder, "Float", DataTypeIds.Float, 1.5f, writable: true);
            CreateVariable(staticFolder, "Double", DataTypeIds.Double, 3.14159, writable: true);
            CreateVariable(staticFolder, "String", DataTypeIds.String, "hello", writable: true);
            CreateVariable(staticFolder, "DateTime", DataTypeIds.DateTime, new DateTime(2026, 1, 1, 13, 30, 0, DateTimeKind.Utc), writable: true);
            CreateVariable(staticFolder, "Guid", DataTypeIds.Guid, new Uuid(Guid.Parse("6f1c1a3e-7b7e-4f43-9d55-1c8c0a6f9e21")), writable: true);
            // Declared as BaseDataType, like servers that don't publish a concrete type: the UI's manual type override is needed to write it.
            CreateVariable(staticFolder, "Variant", DataTypeIds.BaseDataType, 5, writable: true);
            CreateVariable(staticFolder, "ReadOnlyInt32", DataTypeIds.Int32, 99, writable: false);

            var parent = CreateFolder(root, "Deep");
            for (var level = 1; level <= 5; level++)
            {
                parent = CreateFolder(parent, $"Level{level}");
                CreateVariable(parent, $"Value{level}", DataTypeIds.Int32, level, writable: true);
            }

            AddPredefinedNode(SystemContext, root);
        }

        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer?.Dispose();
        base.Dispose(disposing);
    }

    private void Tick()
    {
        lock (Lock)
        {
            _tick++;
            foreach (var (variable, next) in _dynamic)
            {
                variable.Value = next(_tick);
                variable.Timestamp = DateTime.UtcNow;
                variable.ClearChangeMasks(SystemContext, false);
            }
        }
    }

    private void AddDynamic(FolderState parent, string name, NodeId dataType, Func<long, object> next)
    {
        var variable = CreateVariable(parent, name, dataType, next(0), writable: false);
        _dynamic.Add((variable, next));
    }

    private FolderState CreateFolder(NodeState? parent, string name)
    {
        var folder = new FolderState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId(parent is null ? name : $"{parent.NodeId.Identifier}/{name}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText("en", name),
            EventNotifier = EventNotifiers.None
        };
        parent?.AddChild(folder);
        return folder;
    }

    private BaseDataVariableState CreateVariable(NodeState parent, string name, NodeId dataType, object value, bool writable)
    {
        var access = writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead;
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId($"{parent.NodeId.Identifier}/{name}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText("en", name),
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = access,
            UserAccessLevel = access,
            Historizing = false,
            Value = value,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };
        parent.AddChild(variable);
        return variable;
    }
}
