using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AIAssistantExtensions {

static class PackageSkillRegistrar
{
    // The settings UI only displays Project/User/Internal source tags.
    const string SkillTag = "Skills.User.Filesystem.Project";
    const string PackageSkillTag =
      "Skills.User.Filesystem.Project.Package.AIAssistantExtensions";

    const string SkillPath =
      "Packages/jp.keijiro.ai.assistant.extensions/Skills/GameViewCapture/SKILL.md";

    static Action _rescanHandler;
    static bool _registeredRescanHandler;

    [InitializeOnLoadMethod]
    static void Initialize()
    {
        RegisterPackageSkills();
        SubscribeToSkillRescan();
    }

    static void RegisterPackageSkills()
    {
        try
        {
            if (!File.Exists(SkillPath))
                return;

            var skill = CreateSkillFromFile(Path.GetFullPath(SkillPath));
            if (skill == null)
                return;

            AddTag(skill, SkillTag);
            AddTag(skill, PackageSkillTag);
            RemoveExistingSkills();
            AddSkills(skill);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to register AI Assistant extension skills: {ex.Message}");
        }
    }

    static void SubscribeToSkillRescan()
    {
        if (_registeredRescanHandler)
            return;

        var scannerType = FindType("Unity.AI.Assistant.Editor.SkillsScanner");
        var eventInfo = scannerType?.GetEvent
          ("OnSkillsRescanned", BindingFlags.Static | BindingFlags.NonPublic);
        if (eventInfo == null)
            return;

        _rescanHandler = () => EditorApplication.delayCall += RegisterPackageSkills;
        var addMethod = eventInfo.GetAddMethod(true);
        if (addMethod == null)
            return;

        addMethod.Invoke(null, new object[] { _rescanHandler });
        _registeredRescanHandler = true;
    }

    static object CreateSkillFromFile(string fullPath)
    {
        var skillUtilsType = FindType("Unity.AI.Assistant.Skills.SkillUtils");
        if (skillUtilsType == null)
            throw new InvalidOperationException("AI Assistant SkillUtils type was not found.");

        var method = skillUtilsType?.GetMethod
          ("CreateSkillFromFile", BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null)
            throw new InvalidOperationException("AI Assistant SkillUtils.CreateSkillFromFile method was not found.");

        return method.Invoke(null, new object[] { fullPath });
    }

    static void AddTag(object skill, string tag)
    {
        var method = skill.GetType().GetMethod
          ("WithTag", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
            throw new InvalidOperationException("AI Assistant SkillDefinition.WithTag method was not found.");

        method.Invoke(skill, new object[] { tag });
    }

    static void AddSkills(object skill)
    {
        var registryType = FindType("Unity.AI.Assistant.Skills.SkillsRegistry");
        if (registryType == null)
            throw new InvalidOperationException("AI Assistant SkillsRegistry type was not found.");

        var method = registryType?.GetMethod
          ("AddSkills", BindingFlags.Static | BindingFlags.Public);
        if (method == null)
            throw new InvalidOperationException("AI Assistant SkillsRegistry.AddSkills method was not found.");

        var listType = typeof(List<>).MakeGenericType(skill.GetType());
        var list = (IList)Activator.CreateInstance(listType);
        if (list == null)
            throw new InvalidOperationException("Failed to create a SkillDefinition list.");

        list.Add(skill);

        method.Invoke(null, new object[] { list, null });
    }

    static void RemoveExistingSkills()
    {
        var registryType = FindType("Unity.AI.Assistant.Skills.SkillsRegistry");
        if (registryType == null)
            return;

        RemoveByTag(registryType, PackageSkillTag);
    }

    static void RemoveByTag(Type registryType, string tag)
    {
        var method = registryType.GetMethod
          ("RemoveByTag", BindingFlags.Static | BindingFlags.Public);
        if (method == null)
            return;

        method.Invoke(null, new object[] { tag });
    }

    static Type FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName, false);
            if (type != null)
                return type;
        }

        return null;
    }
}

} // namespace AIAssistantExtensions
