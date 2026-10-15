namespace Loom.Core;

/// <summary>A role-filtered UI contract; validation remains authoritative in DocumentService.</summary>
public sealed record DocumentForm(string TypeId,string Name,string Label,bool CanCreate,bool CanEdit,Field[] Fields);
