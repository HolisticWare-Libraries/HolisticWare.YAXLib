﻿// Copyright (C) Sina Iravanian, Julian Verdurmen, axuno gGmbH and other contributors.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Cysharp.Text;
using YAXLib.Pooling.SpecializedPools;

namespace YAXLib;

/// <summary>
/// Provides string utility methods
/// </summary>
internal static class StringUtils
{
    private static readonly char[] SplitChars = [',', ' ', '\t'];

    /// <summary>
    /// Refines the location string. Trims it, and replaces invalid characters with underscore.
    /// </summary>
    /// <param name="elemAddr">The element address to refine.</param>
    /// <returns>the refined location string</returns>
    public static string RefineLocationString(string elemAddr)
    {
        elemAddr = elemAddr.Trim(' ', '\t', '\r', '\n', '\v', '/', '\\');
        if (string.IsNullOrEmpty(elemAddr))
            return ".";

        // replace all back-slashes to slash
        elemAddr = elemAddr.Replace('\\', '/');

        //mc++ using var pooledObject = StringBuilderPool.Instance.Get(out var sb);
        var sb = ZString.CreateStringBuilder();
        //mc++ sb.Capacity = elemAddr.Length;
        var steps = elemAddr.SplitPathNamespaceSafe();
        foreach (var step in steps) sb.Append("/" + RefineSingleElement(step));

        //mc++ return sb.Remove(0, 1).ToString();
        sb.Remove(0, 1);

        return sb.ToString();
    }

    /// <summary>
    /// Heuristically determines if the supplied name conforms to the "expanded XML name" form supported by the
    /// System.Xml.Linq.XName class.
    /// </summary>
    /// <param name="name">The name to be examined.</param>
    /// <returns><c>true</c> if the supplied name appears to be in expanded form, otherwise <c>false</c>.</returns>
    public static bool LooksLikeExpandedXName(string name)
    {
        // XName permits strings of the form '{namespace}localname'. Detecting such cases allows
        // YAXLib to support explicit namespace use.
        // http://msdn.microsoft.com/en-us/library/system.xml.linq.xname.aspx

        name = name.Trim();

        // Needs at least 3 chars ('{}a' is, in theory, valid), must start with '{', must have a following closing '}' which must not be last char.
        if (name.Length >= 3 && (name[0] == '{'))
        {
            var closingBrace = name.IndexOf('}', 1);
            return closingBrace != -1 && closingBrace < name.Length - 1;
        }

        return false;
    }


    /// <summary>
    /// Refines a single element name. Refines the location string. Trims it, and replaces invalid characters with
    /// underscore.
    /// </summary>
    /// <param name="elemName">Name of the element.</param>
    /// <returns>the refined element name</returns>
#if NETSTANDARD2_1_OR_GREATER || NET
    public static string? RefineSingleElement([NotNullIfNotNull(nameof(elemName))] string? elemName)
#else
    public static string? RefineSingleElement(string? elemName)
#endif
    {
        if (elemName == null)
            return null;

        elemName = elemName.Trim(' ', '\t', '\r', '\n', '\v', '/', '\\');
        if (IsSingleLocationGeneric(elemName)) return elemName;

        if (LooksLikeExpandedXName(elemName))
        {
            // thanks go to CodePlex user: tg73 (http://www.codeplex.com/site/users/view/tg73)
            // for providing the code for expanded xml name support

            // XName permits strings of the form '{namespace}localname'. Detecting such cases allows
            // YAXLib to support explicit namespace use.
            // http://msdn.microsoft.com/en-us/library/system.xml.linq.xname.aspx

            // Leave namespace part alone, refine localname part.
            var closingBrace = elemName.IndexOf('}');
            var refinedLocalname = RefineSingleElement(elemName.Substring(closingBrace + 1));
#if NET
            return string.Concat(elemName.AsSpan(0, closingBrace + 1), refinedLocalname);
#else
            return elemName.Substring(0, closingBrace + 1) + refinedLocalname;
#endif
        }

        //mc++ using var pooledObject = StringBuilderPool.Instance.Get(out var sb);
        var sb = ZString.CreateStringBuilder();
        //mc++ sb.Capacity = elemName.Length;

        // This uses the rules defined in http://www.w3.org/TR/xml/#NT-Name.
        // Thanks go to [@asbjornu] for pointing to the W3C standard
        for (var i = 0; i < elemName.Length; i++)
            if (i == 0)
                sb.Append(IsValidNameStartChar(elemName[i]) ? elemName[i] : '_');
            else
                sb.Append(IsValidNameChar(elemName[i]) ? elemName[i] : '_');

        return sb.ToString();
    }

    private static bool IsValidNameStartChar(char ch)
    {
        // This uses the rules defined in http://www.w3.org/TR/xml/#NT-Name.
        // However colon (:) has been removed from the set of allowed characters,
        // because it is reserved for separating namespace prefix and XML-entity names.
        if ( //ch == ':' ||
            ch == '_' ||
            IsInRange(ch, 'A', 'Z') || IsInRange(ch, 'a', 'z') ||
            IsInRange(ch, '\u00C0', '\u00D6') ||
            IsInRange(ch, '\u00D8', '\u00F6') ||
            IsInRange(ch, '\u00F8', '\u02FF') ||
            IsInRange(ch, '\u0370', '\u037D') ||
            IsInRange(ch, '\u037F', '\u1FFF') ||
            IsInRange(ch, '\u200C', '\u200D') ||
            IsInRange(ch, '\u2070', '\u218F') ||
            IsInRange(ch, '\u2C00', '\u2FEF') ||
            IsInRange(ch, '\u3001', '\uD7FF') ||
            IsInRange(ch, '\uF900', '\uFDCF') ||
            IsInRange(ch, '\uFDF0', '\uFFFD')
            //|| IsInRange(ch, '\u10000', '\uEFFFF')
           )
            return true;

        return false;
    }

    private static bool IsValidNameChar(char ch)
    {
        return IsValidNameStartChar(ch) ||
               ch == '-' || ch == '.' ||
               IsInRange(ch, '0', '9') ||
               ch == '\u00B7' ||
               IsInRange(ch, '\u0300', '\u036F')
               || IsInRange(ch, '\u203F', '\u2040');
    }

    private static bool IsInRange(char ch, char lower, char upper)
    {
        return lower <= ch && ch <= upper;
    }


    /// <summary>
    /// Extracts the path and alias from location string.
    /// A pure path location string: level1/level2
    /// A location string augmented with alias: level1/level2#somename
    /// Here path is "level1/level2" and alias is "somename".
    /// </summary>
    /// <param name="locationString">The location string.</param>
    /// <param name="path">The path to be extracted.</param>
    /// <param name="alias">The alias to be extracted.</param>
    public static void ExtractPathAndAliasFromLocationString(string locationString, out string path,
        out string alias)
    {
        var poundIndex = locationString.IndexOf('#');
        if (poundIndex >= 0)
        {
            if (poundIndex == 0)
                path = "";
            else
                path = locationString.Substring(0, poundIndex).Trim();

            if (poundIndex == locationString.Length - 1)
                alias = "";
            else
                alias = locationString.Substring(poundIndex + 1).Trim();
        }
        else
        {
            path = locationString;
            alias = "";
        }
    }


    /// <summary>
    /// Combines a location string and an element name to form a bigger location string.
    /// </summary>
    /// <param name="location">The location string.</param>
    /// <param name="elemName">Name of the element.</param>
    /// <returns>a bigger location string formed by combining a location string and an element name.</returns>
    public static string CombineLocationAndElementName(string location, XName elemName)
    {
        return string.Format("{0}/{1}", location, elemName);
    }

    /// <summary>
    /// Divides the location string one step, to form a shorter location string.
    /// </summary>
    /// <param name="location">The location string to divide.</param>
    /// <param name="newLocation">The new location string which is one level shorter.</param>
    /// <param name="newElem">The element name removed from the end of location string.</param>
    /// <returns></returns>
    public static bool DivideLocationOneStep(string location, out string newLocation, out string newElem)
    {
        newLocation = location;
        newElem = string.Empty;

        var slashIdx = location.LastIndexOf('/');
        if (slashIdx < 0) // no slashes found
        {
            if (IsLocationAllGeneric(location)) return false;

            newElem = location;
            newLocation = ".";
            return true;
        }

        var preSlash = location.Substring(0, slashIdx);
        var postSlash = location.Substring(slashIdx + 1);

        if (IsLocationAllGeneric(postSlash)) return false;

        newLocation = preSlash;
        newElem = postSlash;
        return true;
    }

    /// <summary>
    /// Determines whether the specified location is composed of levels
    /// which are themselves either "." or "..".
    /// </summary>
    /// <param name="location">The location string to check.</param>
    /// <returns>
    /// <c>true</c> if the specified location string is all composed of "." or ".." levels; otherwise, <c>false</c>.
    /// </returns>
    public static bool IsLocationAllGeneric(string location)
    {
        var locSteps = location.SplitPathNamespaceSafe();

        foreach (var loc in locSteps)
            if (!IsSingleLocationGeneric(loc))
                return false;

        return true;
    }

    /// <summary>
    /// Determines whether the specified location string is either "." or "..".
    /// </summary>
    /// <param name="location">The location string to check.</param>
    /// <returns>
    /// <c>true</c> if the specified location string is either "." or ".."; otherwise, <c>false</c>.
    /// </returns>
    public static bool IsSingleLocationGeneric(string location)
    {
        return location == "." || location == "..";
    }

    /// <summary>
    /// Gets the string corresponding to the given array dimensions.
    /// </summary>
    /// <param name="dims">The array dimensions.</param>
    /// <returns>the string corresponding to the given array dimensions</returns>
    public static string GetArrayDimsString(int[] dims)
    {
        //mc++ var sb = new StringBuilder();
        var sb = ZString.CreateStringBuilder();

        for (var i = 0; i < dims.Length; i++)
        {
            if (i != 0)
                sb.Append(',');
            sb.Append(dims[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses the array dimensions string, and returns the corresponding dimensions array.
    /// </summary>
    /// <param name="str">The string to parse.</param>
    /// <returns>the dimensions array corresponding to the given string</returns>
    public static int[] ParseArrayDimsString(string str)
    {
        var strDims = str.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries);
        var lst = new List<int>();
        foreach (var strDim in strDims)
        {
            if (int.TryParse(strDim, out var dim))
                lst.Add(dim);
        }

        return lst.ToArray();
    }

    /// <summary>
    /// Splits a string at each instance of a '/' except where such slashes
    /// are within {}.
    /// </summary>
    /// <param name="value">The string to split</param>
    /// <returns>An enumerable set of strings which were separated by '/'</returns>
    public static IEnumerable<string> SplitPathNamespaceSafe(this string value)
    {
        var bracketCount = 0;
        var lastStart = 0;
        var temp = value;

        if (value.Length <= 1)
        {
            yield return value;
            yield break;
        }

        for (var i = 0; i < temp.Length; i++)
            if (temp[i] == '{')
                bracketCount++;
            else if (temp[i] == '}')
                bracketCount--;
            else if (temp[i] == '/' && bracketCount == 0)
            {
                yield return temp.Substring(lastStart, i - lastStart);
                lastStart = i + 1;
            }

        if (lastStart <= temp.Length - 1)
            yield return temp.Substring(lastStart);
    }

    public static DateTime ParseDateTimeTimeZoneSafe(string str, IFormatProvider formatProvider)
    {
        if (!DateTimeOffset.TryParse(str, formatProvider, DateTimeStyles.None, out var dto))
            return DateTime.MinValue;

        return dto.Offset == TimeSpan.Zero ? dto.UtcDateTime : dto.DateTime;
    }
}
