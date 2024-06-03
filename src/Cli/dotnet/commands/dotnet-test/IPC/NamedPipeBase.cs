﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0240 // Remove redundant nullable directive
#nullable disable
#pragma warning restore IDE0240 // Remove redundant nullable directive

using System.Globalization;

namespace Microsoft.DotNet.Tools.Test;

internal abstract class NamedPipeBase
{
    private readonly Dictionary<Type, object> _typeSerializer = [];
    private readonly Dictionary<int, object> _idSerializer = [];

    public void RegisterSerializer<T>(INamedPipeSerializer namedPipeSerializer)
    {
        _typeSerializer.Add(typeof(T), namedPipeSerializer);
        _idSerializer.Add(namedPipeSerializer.Id, namedPipeSerializer);
    }

    protected INamedPipeSerializer GetSerializer(int id)
        => _idSerializer.TryGetValue(id, out object serializer)
            ? (INamedPipeSerializer)serializer
            : throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "No serializer registered with id '{0}'",
                id));

    protected INamedPipeSerializer GetSerializer(Type type)
        => _typeSerializer.TryGetValue(type, out object serializer)
            ? (INamedPipeSerializer)serializer
            : throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "No serializer registered with type '{0}'",
                type));
}