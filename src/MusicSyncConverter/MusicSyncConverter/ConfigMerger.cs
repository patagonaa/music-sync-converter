using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using MusicSyncConverter.Config.InputModels;
using MusicSyncConverter.Config.OutputModels;

namespace MusicSyncConverter
{
    class ConfigMerger
    {
        public static SyncConfig MergeConfigs(IEnumerable<InputSyncConfig> configs)
        {
            var outputConfig = Activator.CreateInstance<SyncConfig>();

            foreach (var inputConfig in configs)
            {
                BindObject(outputConfig, inputConfig);
            }

            CheckRequiredPropertiesSet(outputConfig);

            return outputConfig;
        }

        private static void BindObject<TOutput, TInput>(TOutput outputConfig, TInput inputConfig)
        {
            var inputType = typeof(TInput);
            var outputType = typeof(TOutput);

            foreach (var inputProp in inputType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var outputProp = outputType.GetProperty(inputProp.Name);
                if (outputProp == null || outputProp.PropertyType == null || inputProp.GetGetMethod() == null)
                    continue;

                // set empty list / dict if it is required
                if (outputProp.GetCustomAttribute<RequiredMemberAttribute>() != null)
                {
                    if (outputProp.PropertyType.IsGenericType &&
                        outputProp.PropertyType.GetGenericTypeDefinition() == typeof(IList<>) &&
                        outputProp.PropertyType == inputProp.PropertyType)
                    {
                        Type listItemType = outputProp.PropertyType.GetGenericArguments()[0];

                        var outputList = outputProp.GetValue(outputConfig);
                        if (outputList is null)
                        {
                            outputList = Activator.CreateInstance(typeof(List<>).MakeGenericType(listItemType));
                            outputProp.SetValue(outputConfig, outputList);
                        }
                    }
                    else if (outputProp.PropertyType.IsGenericType &&
                        outputProp.PropertyType.GetGenericTypeDefinition() == typeof(IDictionary<,>) &&
                        outputProp.PropertyType == inputProp.PropertyType)
                    {
                        Type dictKeyType = outputProp.PropertyType.GetGenericArguments()[0];
                        Type dictValueType = outputProp.PropertyType.GetGenericArguments()[1];

                        var outputDict = outputProp.GetValue(outputConfig);
                        if (outputDict is null)
                        {
                            outputDict = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(dictKeyType, dictValueType));
                            outputProp.SetValue(outputConfig, outputDict);
                        }
                    }
                }

                // override / merge input data into output
                var inputValue = inputProp.GetValue(inputConfig);
                if (inputValue == null)
                    continue;

                if (outputProp.PropertyType.IsClass && (outputProp.PropertyType.Namespace?.Contains("MusicSyncConverter.Config") ?? false))
                {
                    var outputObject = outputProp.GetValue(outputConfig);
                    if (outputObject is null)
                    {
                        outputObject = Activator.CreateInstance(outputProp.PropertyType);
                        outputProp.SetValue(outputConfig, outputObject);
                    }

                    typeof(ConfigMerger)
                        .GetMethod(nameof(BindObject), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(outputProp.PropertyType, inputProp.PropertyType)
                        .Invoke(null, [outputObject, inputValue]);
                }
                else if (outputProp.PropertyType.IsGenericType &&
                    outputProp.PropertyType.GetGenericTypeDefinition() == typeof(IList<>) &&
                    outputProp.PropertyType == inputProp.PropertyType)
                {
                    Type listItemType = outputProp.PropertyType.GetGenericArguments()[0];

                    var outputList = outputProp.GetValue(outputConfig);
                    if (outputList is null)
                    {
                        outputProp.SetValue(outputConfig, inputValue);
                    }
                    else
                    {
                        typeof(ConfigMerger)
                            .GetMethod(nameof(MergeLists), BindingFlags.NonPublic | BindingFlags.Static)!
                            .MakeGenericMethod(listItemType)
                            .Invoke(null, [outputList, inputValue]);
                    }
                }
                else if (outputProp.PropertyType.IsGenericType &&
                    outputProp.PropertyType.GetGenericTypeDefinition() == typeof(IDictionary<,>) &&
                    outputProp.PropertyType == inputProp.PropertyType)
                {
                    Type dictKeyType = outputProp.PropertyType.GetGenericArguments()[0];
                    Type dictValueType = outputProp.PropertyType.GetGenericArguments()[1];

                    var outputDict = outputProp.GetValue(outputConfig);
                    if (outputDict is null)
                    {
                        outputProp.SetValue(outputConfig, inputValue);
                    }

                    typeof(ConfigMerger)
                        .GetMethod(nameof(MergeDicts), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(dictKeyType, dictValueType)
                        .Invoke(null, [outputDict, inputValue]);
                }
                else if (outputProp.PropertyType == inputProp.PropertyType)
                {
                    outputProp.SetValue(outputConfig, inputProp.GetValue(inputConfig));
                }
                else
                {
                    throw new ArgumentException($"Don't know how to merge {inputProp.PropertyType} into {outputProp.PropertyType}");
                }
            }
        }

        private static void MergeLists<T>(IList<T> output, IList<T> input)
        {
            foreach (var item in input)
            {
                output.Add(item);
            }
        }

        private static void MergeDicts<TKey, TValue>(IDictionary<TKey, TValue> output, IDictionary<TKey, TValue> input)
        {
            foreach (var item in input)
            {
                output[item.Key] = item.Value;
            }
        }

        private static void CheckRequiredPropertiesSet<T>(T obj)
        {
            var type = typeof(T);
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (prop.GetCustomAttribute<RequiredMemberAttribute>() != null && prop.GetGetMethod() != null)
                {
                    var value = prop.GetValue(obj);
                    if (value is null)
                    {
                        throw new ArgumentException($"{type.Name}.{prop.Name} is null!");
                    }

                    if (prop.PropertyType.Namespace?.Contains("MusicSyncConverter.Config") ?? false)
                    {
                        typeof(ConfigMerger)
                            .GetMethod(nameof(CheckRequiredPropertiesSet), BindingFlags.NonPublic | BindingFlags.Static)!
                            .MakeGenericMethod(prop.PropertyType)
                            .Invoke(null, [value]);
                    }
                }
            }
        }
    }
}