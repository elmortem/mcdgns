namespace DiagramGenerator.ClassGraph;

public class Graph
{
    public IList<Class> Classes { get; set; } = new List<Class>();
    public IList<ClassRelation> Relations { get; set; } = new List<ClassRelation>();

    public bool AddClass(Class @class)
    {
        if (!Classes.Any(o => o.Fqn == @class.Fqn))
        {
            Classes.Add(@class);
            return true;
        }
        return false;
    }

    public void RebuildRelation(bool inheretanceOnly)
    {
        Relations = new List<ClassRelation>();
        foreach (var @class in Classes)
        {
            var baseClass = Classes.FirstOrDefault(o => o.Fqn == @class.BaseType);

            //inheritance
            if (baseClass != null)
            {
                var relation = new ClassRelation(@class, baseClass, RelationType.Inheritance);
                AddRelation(relation);
            }

            // implementation
            foreach (var intf in @class.ImplementedInterface)
            {
                var intfClass = Classes.FirstOrDefault(o => o.Fqn == intf);

                if (intfClass != null)
                {
                    if (baseClass != null && baseClass.ImplementedInterface.Contains(intf))
                    {
                        continue;
                    }
                    var relation = new ClassRelation(@class, intfClass, RelationType.Implementation);
                    AddRelation(relation);
                }
            }

            // dependency
            if (!inheretanceOnly)
            {
                foreach (var prop in @class.Properties)
                {
                    var depdList = prop.TypeParams;
                    if (prop.Type != null)
                    {
                        depdList.Add(prop.Type);
                    }
                    depdList = depdList.Distinct().ToList();

                    foreach (var depdType in depdList)
                    {
                        var depdClass = Classes.FirstOrDefault(o => o.Fqn == depdType);
                        if (depdClass != null)
                        {
                            var relation = new ClassRelation(@class, depdClass, RelationType.Dependency);
                            AddRelation(relation);
                        }
                    }
                }
            }
        }
    }

    public bool AddRelation(ClassRelation relation)
    {
        if (!Relations.Any(o => o.From.Fqn == relation.From.Fqn && o.To.Fqn == relation.To.Fqn && o.Type == relation.Type))
        {
            Relations.Add(relation);
            return true;
        }
        return false;
    }
}

public class ClassRelation
{
    public Class From { get; set; }
    public Class To { get; set; }
    public RelationType Type { get; set; }


    public ClassRelation(Class from, Class to, RelationType type)
    {
        From = from;
        To = to;
        Type = type;
    }

}

public class Class
{
    public string Name { get; set; }
    public string Namespace { get; set; } = string.Empty;
    public string Fqn => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
    public string? BaseType { get; set; }
    public IList<string> ImplementedInterface { get; set; } = new List<string>();
    public bool IsInterface { get; set; } = false;
    public TypeKind Kind { get; set; } = TypeKind.Class;
    public IList<Property> Properties { get; set; } = new List<Property>();
    public IList<Method> Methods { get; set; } = new List<Method>();
    public IList<string> EnumValues { get; set; } = new List<string>();

    public Class(string name)
    {
        Name = name;
    }

    public bool AddProperty(Property property)
    {
        if (!Properties.Any(o => o.Name == property.Name))
        {
            Properties.Add(property);
            return true;
        }
        return false;
    }

    public bool AddMethod(Method method)
    {
        if (!Methods.Any(o => o.Name == method.Name))
        {
            Methods.Add(method);
            return true;
        }
        return false;
    }
}

public class Property : Member
{
    public string? Type { get; set; }
    public string? GenericType { get; set; }
    public IList<string> TypeParams { get; set; } = new List<string>();

    public Property(string name, Visibility visibility) : base(name, visibility)
    {
    }
}

public class Method : Member
{
    public string? Type { get; set; }
    public string? GenericType { get; set; }
    public IList<string> TypeParams { get; set; } = new List<string>();

    public Method(string name, Visibility visibility) : base(name, visibility)
    {
    }
}

public class Member
{
    public string Name { get; set; }
    public Visibility MemberVisibility { get; set; }

    public Member(string name, Visibility visibility)
    {
        Name = name;
        MemberVisibility = visibility;
    }
}

public enum Visibility
{
    Private = 0,
    Internal,
    Protected,
    Public
}
public enum RelationType
{
    Implementation = 0,
    Inheritance,
    Dependency
}

public enum TypeKind
{
    Class = 0,
    Interface,
    Record,
    Struct,
    RecordStruct,
    Enum
}