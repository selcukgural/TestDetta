using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>Member signatures turned into the method names a coverage map records.</summary>
public sealed class CompiledNamesTests
{
    [Theory]
    [InlineData("Place(string,int)", "Place")]
    [InlineData("IXmlSerializable.ReadXml(XmlReader)", "ReadXml")]
    [InlineData("Total", "Total")]
    [InlineData(".ctor(int)", ".ctor")]
    [InlineData("Finalize()", "Finalize")]
    [InlineData("operator +(Money,Money)", "op_Addition,op_CheckedAddition")]
    [InlineData("operator -(Money)", "op_UnaryNegation,op_CheckedUnaryNegation")]
    [InlineData("operator +(Money)", "op_UnaryPlus")]
    [InlineData("operator ==(Money,Money)", "op_Equality")]
    [InlineData("operator >>>(Money,int)", "op_UnsignedRightShift")]
    [InlineData("operator +(Dictionary<int,string>)", "op_UnaryPlus")]
    [InlineData("operator true(Money)", "op_True")]
    [InlineData("operator implicit decimal(Money)", "op_Implicit")]
    [InlineData("operator explicit Money(decimal)", "op_Explicit,op_CheckedExplicit")]
    public void Member_signature_gives_its_compiled_names(string member, string names) =>
        Assert.Equal(names.Split(','), DeclarationWalker.CompiledNames(member));

    [Theory]
    [InlineData("this[int]")]
    [InlineData("IList.this[int]")]
    [InlineData("_a,_b")]
    public void Names_the_signature_cannot_tell_are_unknown(string member) =>
        Assert.Empty(DeclarationWalker.CompiledNames(member));
}
