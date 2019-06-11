﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using FomodInstaller.Interface;

namespace FomodInstaller.Scripting.XmlScript
{
    /// <summary>
    /// Performs the mod installation based on the XML script.
    /// </summary>
    public class XmlScriptInstaller // ??? : BackgroundTask
    {
        private List<Instruction> modInstallInstructions = new List<Instruction>();

        #region Properties

        private Mod ModArchive;

        #endregion

        #region Constructors

        /// <summary>
        /// A simple constructor that initializes the object with the required dependencies.
        /// </summary>
        /// <param name="modArchive">The mod for which the script is running.</param>
        public XmlScriptInstaller(Mod modArchive)
        {
            ModArchive = modArchive;
        }

        #endregion

        /// <summary>
        /// Performs the mod installation based on the XML script.
        /// </summary>
        /// <param name="xscScript">The script that is executing.</param>
        /// <param name="coreDelegates">The Core delegates component.</param>
        /// <param name="filesToInstall">The list of files to install.</param>
        /// <param name="pluginsToActivate">The list of plugins to activate.</param>
        /// <returns><c>true</c> if the installation succeeded;
        /// <c>false</c> otherwise.</returns>
        public IList<Instruction> Install(XmlScript xscScript, ConditionStateManager csmState, CoreDelegates coreDelegates, IEnumerable<InstallableFile> filesToInstall, ICollection<InstallableFile> pluginsToActivate)
        {
            try
            {
                InstallFiles(xscScript, csmState, coreDelegates, filesToInstall, pluginsToActivate);
            }
            catch (Exception ex)
            {
                modInstallInstructions.Add(Instruction.InstallError("fatal", ex.Message));
            }
            return modInstallInstructions;
        }

        /// <summary>
        /// Installs and activates files are required. This method is used by the background worker.
        /// </summary>
        /// <param name="xscScript">The script that is executing.</param>
        /// <param name="coreDelegates">The Core delegates component.</param>
        /// <param name="filesToInstall">The list of files to install.</param>
        /// <param name="pluginsToActivate">The list of plugins to activate.</param>
        protected bool InstallFiles(XmlScript xscScript, ConditionStateManager csmState, CoreDelegates coreDelegates, IEnumerable<InstallableFile> filesToInstall, ICollection<InstallableFile> pluginsToActivate)
        {
            bool HadIssues = false;
            IList<InstallableFile> lstRequiredFiles = xscScript.RequiredInstallFiles;
            IList<ConditionallyInstalledFileSet> lstConditionallyInstalledFileSets = xscScript.ConditionallyInstalledFileSets;

            foreach (InstallableFile iflRequiredFile in lstRequiredFiles)
            {
                if (!InstallFile(iflRequiredFile))
                    HadIssues = true;
            }

            if (!HadIssues)
            {
                foreach (InstallableFile ilfFile in filesToInstall)
                {
                    if (!InstallFile(ilfFile))
                        HadIssues = true;
                }
            }

            if (!HadIssues)
            {
                foreach (ConditionallyInstalledFileSet cisFileSet in lstConditionallyInstalledFileSets)
                {
                    if (cisFileSet.Condition.GetIsFulfilled(csmState, coreDelegates))
                        foreach (InstallableFile ilfFile in cisFileSet.Files)
                        {
                            if (!InstallFile(ilfFile))
                                HadIssues = true;
                        }
                }
            }

            if (!HadIssues)
                modInstallInstructions.Add(Instruction.EnableAllPlugins());

            return !HadIssues;
        }

        /// <summary>
        /// Installs the given <see cref="InstallableFile"/>, and activates any
        /// plugins it encompasses as requested.
        /// </summary>
        /// <param name="p_ilfFile">The file to install.</param>
        /// <returns><c>false</c> if the user cancelled the install;
        /// <c>true</c> otherwise.</returns>
        protected bool InstallFile(InstallableFile installableFile)
        {
            if (installableFile.IsFolder)
            {
                if (!InstallFolderFromMod(installableFile, ModArchive.Prefix))
                    return false;
            }
            else
            {
                string strSource = Path.Combine(ModArchive.Prefix, installableFile.Source);
                int count = ModArchive.GetFileList(strSource, true, false).Count;
                if (count == 1)
                {
                    string strDest = installableFile.Destination;
                    InstallFileFromMod(strSource, strDest, installableFile.Priority);
                }
                else
                {
                    modInstallInstructions.Add(Instruction.InstallError("warning", count == 0
                        ? "Source doesn't match any files: \"" + strSource + "\""
                        : "Source matches a directory, was supposed to be a file: \"" + strSource + "\""));
                }
            }

            return true;
        }

        #region Helper Methods

        /// <summary>
        /// Recursively copies all files and folders from one location to another.
        /// </summary>
        /// <param name="installableFile">The folder to install.</param>
        /// <returns><c>false</c> if the user cancelled the install;
        /// <c>true</c> otherwise.</returns>
        protected bool InstallFolderFromMod(InstallableFile installableFile, string strPrefixPath)
        {
            IList<string> lstModFiles = ModArchive.GetFileList(Path.Combine(strPrefixPath, installableFile.Source), true, false);

            string strFrom = Path.Combine(strPrefixPath, installableFile.Source).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (!strFrom.EndsWith(Path.DirectorySeparatorChar.ToString()))
                strFrom += Path.DirectorySeparatorChar;
            string strTo = installableFile.Destination.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if ((strTo.Length > 0) && (!strTo.EndsWith(Path.DirectorySeparatorChar.ToString())))
                strTo += Path.DirectorySeparatorChar;
            string strMODFile = null;
            for (int i = 0; i < lstModFiles.Count; i++)
            {
                strMODFile = lstModFiles[i];
                int intLength = strMODFile.Length - strFrom.Length;
                if (intLength <= 0)
                {
                    throw new Exception("Failed to install \"" + strFrom + "\" as folder");
                }
                string strNewFileName = strMODFile.Substring(strFrom.Length, intLength);
                if (strTo.Length > 0)
                    strNewFileName = Path.Combine(strTo, strNewFileName);
                InstallFileFromMod(strMODFile, strNewFileName, installableFile.Priority);
            }
            return true;
        }

        /// <summary>
        /// Installs the specified file from the mod to the specified location on the file system.
        /// </summary>
        /// <param name="fromPath">The path of the file in the mod to install.</param>
        /// <param name="toPath">The path on the file system where the file is to be created.</param>
        /// <returns><c>true</c> if the file was written; <c>false</c> otherwise.</returns>
        protected bool InstallFileFromMod(string fromPath, string toPath, int priority)
        {
            bool booSuccess = false;

            if (toPath.EndsWith("" + Path.AltDirectorySeparatorChar)
                || toPath.EndsWith("" + Path.DirectorySeparatorChar))
            {
                modInstallInstructions.Add(Instruction.CreateMKDir(toPath));

            } else
            {
                if (toPath == "")
                {
                    toPath = Path.GetFileName(fromPath);
                }
                int idx = modInstallInstructions.FindIndex(instruction => (instruction.type == "copy")
                                                                       && (instruction.destination == toPath));
                if (idx == -1)
                {
                  modInstallInstructions.Add(Instruction.CreateCopy(fromPath, toPath, priority));
                } else if (shouldUpdate(modInstallInstructions[idx], fromPath, priority))
                {
                  modInstallInstructions[idx] = Instruction.CreateCopy(fromPath, toPath, priority);
                } // otherwise leave the file in there
            }

            booSuccess = true;

            return booSuccess;
        }

        private bool shouldUpdate(Instruction oldInstruction, string fromPath, int priority)
        {
          if (priority != oldInstruction.priority)
          {
            return priority > oldInstruction.priority;
          }

          return fromPath.CompareTo(oldInstruction.source) > 0;
        }

        #endregion
    }
}
