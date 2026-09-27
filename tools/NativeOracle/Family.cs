namespace NativeOracle;

/// <summary>One named entry of the instruction table: a mnemonic and the generator that builds one random case of it at a time.</summary>
public sealed record Family(string Name, Func<FamilyContext, TestCase> Generate);
