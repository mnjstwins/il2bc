﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.Emit;

namespace Microsoft.CodeAnalysis.CSharp.Emit
{
    /// <summary>
    /// Represents a reference to a type nested in an instantiation of a generic type.
    /// e.g. 
    /// A{int}.B
    /// A.B{int}.C.D
    /// </summary>
    internal class SpecializedNestedTypeReference : NamedTypeReference, Cci.ISpecializedNestedTypeReference
    {
        public SpecializedNestedTypeReference(NamedTypeSymbol underlyingNamedType)
            : base(underlyingNamedType)
        {
        }

        Cci.INestedTypeReference Cci.ISpecializedNestedTypeReference.UnspecializedVersion
        {
            get
            {
                System.Diagnostics.Debug.Assert(UnderlyingNamedType.OriginalDefinition.IsDefinition);
                return this.UnderlyingNamedType.OriginalDefinition;
            }
        }

        public override void Dispatch(Cci.MetadataVisitor visitor)
        {
            visitor.Visit((Cci.ISpecializedNestedTypeReference)this);
        }

        Cci.ITypeReference Cci.ITypeMemberReference.GetContainingType(EmitContext context)
        {
            return ((PEModuleBuilder)context.Module).Translate(UnderlyingNamedType.ContainingType, (CSharpSyntaxNode)context.SyntaxNodeOpt, context.Diagnostics);
        }

        public override Cci.IGenericTypeInstanceReference AsGenericTypeInstanceReference
        {
            get { return null; }
        }

        public override Cci.INamespaceTypeReference AsNamespaceTypeReference
        {
            get { return null; }
        }

        public override Cci.INestedTypeReference AsNestedTypeReference
        {
            get { return this; }
        }

        public override Cci.ISpecializedNestedTypeReference AsSpecializedNestedTypeReference
        {
            get { return this; }
        }
    }
}
