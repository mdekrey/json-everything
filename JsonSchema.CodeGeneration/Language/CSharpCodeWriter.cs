﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Humanizer;
using Json.Schema.CodeGeneration.Model;

namespace Json.Schema.CodeGeneration.Language;

internal class CSharpCodeWriter : ICodeWriter
{
	private static string? Transform(string? original) => original?.Underscore().Pascalize();

	public void Write(StringBuilder builder, TypeModel model)
	{
		var allModels = CollectModels(model)
			.Distinct()
			.GroupBy(x => Transform(x.Name))
			.ToArray();
		var duplicateNames = allModels.Where(x => x.Key != null && x.Count() != 1);

		// ReSharper disable PossibleMultipleEnumeration
		if (duplicateNames.Any())
		{
			var names = string.Join(",", duplicateNames.Select(x => x.Key));
			// ReSharper restore PossibleMultipleEnumeration
			throw new UnsupportedSchemaException($"Found multiple definitions for the names [{names}]");
		}

		foreach (var singleModel in allModels.Where(x => x.Key != null))
		{
			WriteDeclaration(builder, singleModel.Single());
		}
	}

	private static IEnumerable<TypeModel> CollectModels(TypeModel model)
	{
		var found = new List<TypeModel>();
		var toCheck = new Queue<TypeModel>();
		toCheck.Enqueue(model);
		while (toCheck.Count != 0)
		{
			var current = toCheck.Dequeue();
			if (found.Contains(current)) continue;

			found.Add(current);
			switch (current)
			{
				case ArrayModel arrayModel:
					toCheck.Enqueue(arrayModel.Items);
					break;
				case ObjectModel objectModel:
					foreach (var propertyModel in objectModel.Properties)
					{
						toCheck.Enqueue(propertyModel.Type);
					}
					break;
				case DictionaryModel dictionaryModel:
					toCheck.Enqueue(dictionaryModel.Keys);
					toCheck.Enqueue(dictionaryModel.Items); 
					break;
			}
		}

		return found;
	}

	private static void WriteUsage(StringBuilder builder, TypeModel model)
	{
		if (model.IsSimple)
		{
			if (ReferenceEquals(model, CommonModels.String))
				builder.Append("string");
			else if (ReferenceEquals(model, CommonModels.Integer))
				builder.Append("int");
			else if (ReferenceEquals(model, CommonModels.Number))
				builder.Append("double");
			else if (ReferenceEquals(model, CommonModels.Boolean))
				builder.Append("bool");
			else
				throw new ArgumentOutOfRangeException(nameof(model));
			return;
		}

		if (model.Name != null)
		{
			builder.Append(Transform(model.Name));
			return;
		}

		// only arrays and dictionaries can opt out of names
		switch (model)
		{
			case ArrayModel arrayModel:
				WriteUsage(builder, arrayModel);
				break;
			case DictionaryModel dictionaryModel:
				WriteUsage(builder, dictionaryModel);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(model));
		}
	}

	private static void WriteDeclaration(StringBuilder builder, TypeModel model)
	{
		if (model.Name == null) return;
		if (model.IsSimple) return;

		switch (model)
		{
			case EnumModel enumModel:
				WriteDeclaration(builder, enumModel);
				break;
			case ArrayModel arrayModel:
				WriteDeclaration(builder, arrayModel);
				break;
			case ObjectModel objectModel:
				WriteDeclaration(builder, objectModel);
				break;
			case DictionaryModel dictionaryModel:
				WriteDeclaration(builder, dictionaryModel);
				break;
			default:
				if (ReferenceEquals(model, CommonModels.String))
					builder.Append(CommonModels.String.Name);
				else if (ReferenceEquals(model, CommonModels.Integer))
					builder.Append(CommonModels.Integer.Name);
				else if (ReferenceEquals(model, CommonModels.Number))
					builder.Append(CommonModels.Number.Name);
				else if (ReferenceEquals(model, CommonModels.Boolean))
					builder.Append(CommonModels.Boolean.Name);
				else
					throw new ArgumentOutOfRangeException(nameof(model));
				break;
		}
	}

	private static void WriteDeclaration(StringBuilder builder, EnumModel model)
	{
		void WriteValue(EnumValue value)
		{
			builder.Append("\t");
			builder.Append(Transform(value.Name));
			builder.Append(" = ");
			builder.Append(value.Value);
		}

		builder.Append("public enum ");
		builder.AppendLine(Transform(model.Name));
		builder.AppendLine("{");
		for (var i = 0; i < model.Values.Length - 1; i++)
		{
			var value = model.Values[i];
			WriteValue(value);
			builder.AppendLine(",");
		}
		WriteValue(model.Values[model.Values.Length - 1]);
		builder.AppendLine();
		builder.AppendLine("}");
	}

	private static void WriteUsage(StringBuilder builder, ArrayModel model)
	{
		WriteUsage(builder, model.Items);
		builder.Append("[]");
	}

	private static void WriteDeclaration(StringBuilder builder, ArrayModel model)
	{
		builder.Append("public class ");
		builder.Append(Transform(model.Name));
		builder.Append(" : List<");
		WriteUsage(builder, model.Items);
		builder.AppendLine(">");
		builder.AppendLine("{");
		builder.AppendLine("}");
	}

	private static void WriteDeclaration(StringBuilder builder, ObjectModel model)
	{
		builder.Append("public class ");
		builder.AppendLine(Transform(model.Name));
		builder.AppendLine("{");
		foreach (var property in model.Properties)
		{
			builder.Append("\tpublic ");
			WriteUsage(builder, property.Type);
			builder.Append(" ");
			builder.Append(Transform(property.Name));
			builder.Append(" { ");
			if (property.CanRead)
				builder.Append("get; ");
			if (property.CanWrite)
				builder.Append("set; ");
			builder.AppendLine("}");
		}
		builder.AppendLine("}");
	}

	private static void WriteUsage(StringBuilder builder, DictionaryModel model)
	{
		builder.Append("Dictionary<");
		WriteUsage(builder, model.Keys);
		builder.Append(", ");
		WriteUsage(builder, model.Items);
		builder.Append(">");
	}

	private static void WriteDeclaration(StringBuilder builder, DictionaryModel model)
	{
		builder.Append("public class ");
		builder.Append(Transform(model.Name));
		builder.Append(" : Dictionary<");
		WriteUsage(builder, model.Keys);
		builder.Append(", ");
		WriteUsage(builder, model.Items);
		builder.AppendLine(">");
		builder.AppendLine("{");
		builder.AppendLine("}");
	}
}