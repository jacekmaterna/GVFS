﻿using GVFS.FunctionalTests.FileSystemRunners;
using GVFS.FunctionalTests.Should;
using GVFS.FunctionalTests.Tests.EnlistmentPerTestCase;
using GVFS.FunctionalTests.Tools;
using GVFS.Tests.Should;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GVFS.FunctionalTests.Tests.MultiEnlistmentTests
{
    [TestFixture]
    [Category(Categories.FullSuiteOnly)]
    public class SharedCacheTests : TestsWithMultiEnlistment
    {
        private const string WellKnownFile = "Readme.md";

        // This branch and commit sha should point to the same place.
        private const string WellKnownBranch = "FunctionalTests/20170602";
        private const string WellKnownCommitSha = "b407df4e21261e2bf022ef7031fabcf21ee0e14d";

        private string localCachePath;
        private string localCacheParentPath;

        private FileSystemRunner fileSystem;

        public SharedCacheTests()
        {
            this.fileSystem = new SystemIORunner();
        }

        [SetUp]
        public void SetCacheLocation()
        {
            this.localCacheParentPath = Path.Combine(Properties.Settings.Default.EnlistmentRoot, "..", Guid.NewGuid().ToString("N"));
            this.localCachePath = Path.Combine(this.localCacheParentPath, ".customGVFSCache");
        }

        [TestCase]
        public void SecondCloneDoesNotDownloadAdditionalObjects()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();
            File.ReadAllText(Path.Combine(enlistment1.RepoRoot, WellKnownFile));

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment1);

            string[] allObjects = Directory.EnumerateFiles(enlistment1.LocalCacheRoot, "*", SearchOption.AllDirectories).ToArray();

            GVFSFunctionalTestEnlistment enlistment2 = this.CloneAndMountEnlistment();
            File.ReadAllText(Path.Combine(enlistment2.RepoRoot, WellKnownFile));

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment2);

            enlistment2.LocalCacheRoot.ShouldEqual(enlistment1.LocalCacheRoot, "Sanity: Local cache roots are expected to match.");
            Directory.EnumerateFiles(enlistment2.LocalCacheRoot, "*", SearchOption.AllDirectories)
                .ShouldMatchInOrder(allObjects);
        }

        [TestCase]
        public void RepairFixesCorruptBlobSizesDatabase()
        {
            GVFSFunctionalTestEnlistment enlistment = this.CloneAndMountEnlistment();
            enlistment.UnmountGVFS();

            // Repair on a healthy enlistment should succeed
            enlistment.Repair();

            string blobSizesRoot = GVFSHelpers.GetPersistedBlobSizesRoot(enlistment.DotGVFSRoot).ShouldNotBeNull();
            string blobSizesDbPath = Path.Combine(blobSizesRoot, "BlobSizes.sql");
            blobSizesDbPath.ShouldBeAFile(this.fileSystem);
            this.fileSystem.WriteAllText(blobSizesDbPath, "0000");

            enlistment.TryMountGVFS().ShouldEqual(false, "GVFS shouldn't mount when blob size db is corrupt");
            enlistment.Repair();
            enlistment.MountGVFS();
        }

        [TestCase]
        public void MountUpgradesLocalSizesToSharedCache()
        {
            GVFSFunctionalTestEnlistment enlistment = this.CloneAndMountEnlistment();
            enlistment.UnmountGVFS();

            string localCacheRoot = GVFSHelpers.GetPersistedLocalCacheRoot(enlistment.DotGVFSRoot);
            string gitObjectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment.DotGVFSRoot);

            // Delete the existing repo metadata
            string versionJsonPath = Path.Combine(enlistment.DotGVFSRoot, GVFSHelpers.RepoMetadataName);
            versionJsonPath.ShouldBeAFile(this.fileSystem);
            this.fileSystem.DeleteFile(versionJsonPath);

            // "13.0" was the last version before blob sizes were moved out of Esent
            string metadataPath = Path.Combine(enlistment.DotGVFSRoot, GVFSHelpers.RepoMetadataName);
            this.fileSystem.CreateEmptyFile(metadataPath);
            GVFSHelpers.SaveDiskLayoutVersion(enlistment.DotGVFSRoot, "13", "0");
            GVFSHelpers.SaveLocalCacheRoot(enlistment.DotGVFSRoot, localCacheRoot);
            GVFSHelpers.SaveGitObjectsRoot(enlistment.DotGVFSRoot, gitObjectsRoot);

            // Create a legacy PersistedDictionary sizes database
            List<KeyValuePair<string, long>> entries = new List<KeyValuePair<string, long>>()
            {
                new KeyValuePair<string, long>(new string('0', 40), 1),
                new KeyValuePair<string, long>(new string('1', 40), 2),
                new KeyValuePair<string, long>(new string('2', 40), 4),
                new KeyValuePair<string, long>(new string('3', 40), 8),
            };

            GVFSHelpers.CreateEsentBlobSizesDatabase(enlistment.DotGVFSRoot, entries);

            enlistment.MountGVFS();

            string majorVersion;
            string minorVersion;
            GVFSHelpers.GetPersistedDiskLayoutVersion(enlistment.DotGVFSRoot, out majorVersion, out minorVersion);

            majorVersion
                .ShouldBeAnInt("Disk layout version should always be an int")
                .ShouldEqual(DiskLayoutUpgradeTests.CurrentDiskLayoutMajorVersion, "Disk layout version should be upgraded to the latest");

            minorVersion
                .ShouldBeAnInt("Disk layout version should always be an int")
                .ShouldEqual(DiskLayoutUpgradeTests.CurrentDiskLayoutMinorVersion, "Disk layout version should be upgraded to the latest");

            string newBlobSizesRoot = Path.Combine(Path.GetDirectoryName(gitObjectsRoot), DiskLayoutUpgradeTests.BlobSizesCacheName);
            GVFSHelpers.GetPersistedBlobSizesRoot(enlistment.DotGVFSRoot)
                .ShouldEqual(newBlobSizesRoot);

            string blobSizesDbPath = Path.Combine(newBlobSizesRoot, DiskLayoutUpgradeTests.BlobSizesDBFileName);
            newBlobSizesRoot.ShouldBeADirectory(this.fileSystem);
            blobSizesDbPath.ShouldBeAFile(this.fileSystem);

            foreach (KeyValuePair<string, long> entry in entries)
            {
                GVFSHelpers.SQLiteBlobSizesDatabaseHasEntry(blobSizesDbPath, entry.Key, entry.Value);
            }

            // Upgrade a second repo, and make sure all sizes from both upgrades are in the shared database

            GVFSFunctionalTestEnlistment enlistment2 = this.CloneAndMountEnlistment();
            enlistment2.UnmountGVFS();

            // Delete the existing repo metadata
            versionJsonPath = Path.Combine(enlistment2.DotGVFSRoot, GVFSHelpers.RepoMetadataName);
            versionJsonPath.ShouldBeAFile(this.fileSystem);
            this.fileSystem.DeleteFile(versionJsonPath);

            // "13.0" was the last version before blob sizes were moved out of Esent
            metadataPath = Path.Combine(enlistment2.DotGVFSRoot, GVFSHelpers.RepoMetadataName);
            this.fileSystem.CreateEmptyFile(metadataPath);
            GVFSHelpers.SaveDiskLayoutVersion(enlistment2.DotGVFSRoot, "13", "0");
            GVFSHelpers.SaveLocalCacheRoot(enlistment2.DotGVFSRoot, localCacheRoot);
            GVFSHelpers.SaveGitObjectsRoot(enlistment2.DotGVFSRoot, gitObjectsRoot);

            // Create a legacy PersistedDictionary sizes database
            List<KeyValuePair<string, long>> additionalEntries = new List<KeyValuePair<string, long>>()
            {
                new KeyValuePair<string, long>(new string('4', 40), 16),
                new KeyValuePair<string, long>(new string('5', 40), 32),
                new KeyValuePair<string, long>(new string('6', 40), 64),
            };

            GVFSHelpers.CreateEsentBlobSizesDatabase(enlistment2.DotGVFSRoot, additionalEntries);

            enlistment2.MountGVFS();

            foreach (KeyValuePair<string, long> entry in entries)
            {
                GVFSHelpers.SQLiteBlobSizesDatabaseHasEntry(blobSizesDbPath, entry.Key, entry.Value);
            }

            foreach (KeyValuePair<string, long> entry in additionalEntries)
            {
                GVFSHelpers.SQLiteBlobSizesDatabaseHasEntry(blobSizesDbPath, entry.Key, entry.Value);
            }
        }

        [TestCase]
        public void CloneCleansUpStaleMetadataLock()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();
            string metadataLockPath = Path.Combine(this.localCachePath, "mapping.dat.lock");
            metadataLockPath.ShouldNotExistOnDisk(this.fileSystem);
            this.fileSystem.WriteAllText(metadataLockPath, enlistment1.EnlistmentRoot);
            metadataLockPath.ShouldBeAFile(this.fileSystem);

            GVFSFunctionalTestEnlistment enlistment2 = this.CloneAndMountEnlistment();
            metadataLockPath.ShouldNotExistOnDisk(this.fileSystem);

            enlistment1.Status().ShouldContain("Mount status: Ready");
            enlistment2.Status().ShouldContain("Mount status: Ready");
        }
        
        [TestCase]
        public void ParallelReadsInASharedCache()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();
            GVFSFunctionalTestEnlistment enlistment2 = this.CloneAndMountEnlistment();
            GVFSFunctionalTestEnlistment enlistment3 = null;

            Task task1 = Task.Run(() => this.HydrateEntireRepo(enlistment1));
            Task task2 = Task.Run(() => this.HydrateEntireRepo(enlistment2));
            Task task3 = Task.Run(() => enlistment3 = this.CloneAndMountEnlistment());

            task1.Wait();
            task2.Wait();
            task3.Wait();

            task1.Exception.ShouldBeNull();
            task2.Exception.ShouldBeNull();
            task3.Exception.ShouldBeNull();

            enlistment1.Status().ShouldContain("Mount status: Ready");
            enlistment2.Status().ShouldContain("Mount status: Ready");
            enlistment3.Status().ShouldContain("Mount status: Ready");

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment1);
            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment2);
            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment3);
        }

        [TestCase]
        public void DeleteObjectsCacheAndCacheMappingBeforeMount()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();
            GVFSFunctionalTestEnlistment enlistment2 = this.CloneAndMountEnlistment();

            enlistment1.UnmountGVFS();

            string objectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment1.DotGVFSRoot).ShouldNotBeNull();
            objectsRoot.ShouldBeADirectory(this.fileSystem);
            CmdRunner.DeleteDirectoryWithRetry(objectsRoot);

            string metadataPath = Path.Combine(this.localCachePath, "mapping.dat");
            metadataPath.ShouldBeAFile(this.fileSystem);
            this.fileSystem.DeleteFile(metadataPath);

            enlistment1.MountGVFS();

            Task task1 = Task.Run(() => this.HydrateRootFolder(enlistment1));
            Task task2 = Task.Run(() => this.HydrateRootFolder(enlistment2));
            task1.Wait();
            task2.Wait();
            task1.Exception.ShouldBeNull();
            task2.Exception.ShouldBeNull();

            enlistment1.Status().ShouldContain("Mount status: Ready");
            enlistment2.Status().ShouldContain("Mount status: Ready");

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment1);
            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment2);
        }

        [TestCase]
        public void DeleteCacheDuringHydrations()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();

            string objectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment1.DotGVFSRoot).ShouldNotBeNull();
            objectsRoot.ShouldBeADirectory(this.fileSystem);

            Task task1 = Task.Run(() =>
            {
                this.HydrateEntireRepo(enlistment1);
            });

            while (!task1.IsCompleted)
            {
                try
                {
                    // Delete objectsRoot rather than this.localCachePath as the blob sizes database cannot be deleted while GVFS is mounted
                    CmdRunner.DeleteDirectoryWithRetry(objectsRoot);
                    Thread.Sleep(100);
                }
                catch (IOException)
                {
                    // Hydration may have handles into the cache, so failing this delete is expected.
                }
            }

            task1.Exception.ShouldBeNull();

            enlistment1.Status().ShouldContain("Mount status: Ready");
        }

        [TestCase]
        public void DownloadingACommitWithoutTreesDoesntBreakNextClone()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();
            GitProcess.Invoke(enlistment1.RepoRoot, "cat-file -s " + WellKnownCommitSha).ShouldEqual("293\n");

            GVFSFunctionalTestEnlistment enlistment2 = this.CloneAndMountEnlistment(WellKnownBranch);
            enlistment2.Status().ShouldContain("Mount status: Ready");
        }

        [TestCase]
        public void MountReusesLocalCacheKeyWhenGitObjectsRootDeleted()
        {
            GVFSFunctionalTestEnlistment enlistment = this.CloneAndMountEnlistment();

            enlistment.UnmountGVFS();

            // Find the current git objects root and ensure it's on disk
            string objectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment.DotGVFSRoot).ShouldNotBeNull();
            objectsRoot.ShouldBeADirectory(this.fileSystem);

            string mappingFilePath = Path.Combine(enlistment.LocalCacheRoot, "mapping.dat");
            string mappingFileContents = this.fileSystem.ReadAllText(mappingFilePath);
            mappingFileContents.Length.ShouldNotEqual(0, "mapping.dat should not be empty");

            // Delete the git objects root folder, mount should re-create it and the mapping.dat file should not change
            CmdRunner.DeleteDirectoryWithRetry(objectsRoot);

            enlistment.MountGVFS();

            GVFSHelpers.GetPersistedGitObjectsRoot(enlistment.DotGVFSRoot).ShouldEqual(objectsRoot);
            objectsRoot.ShouldBeADirectory(this.fileSystem);
            mappingFilePath.ShouldBeAFile(this.fileSystem).WithContents(mappingFileContents);

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment);
        }

        [TestCase]
        public void MountUsesNewLocalCacheKeyWhenLocalCacheDeleted()
        {
            GVFSFunctionalTestEnlistment enlistment = this.CloneAndMountEnlistment();

            enlistment.UnmountGVFS();

            // Find the current git objects root and ensure it's on disk
            string objectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment.DotGVFSRoot).ShouldNotBeNull();
            objectsRoot.ShouldBeADirectory(this.fileSystem);

            string mappingFilePath = Path.Combine(enlistment.LocalCacheRoot, "mapping.dat");
            string mappingFileContents = this.fileSystem.ReadAllText(mappingFilePath);
            mappingFileContents.Length.ShouldNotEqual(0, "mapping.dat should not be empty");

            // Delete the local cache folder, mount should re-create it and generate a new mapping file and local cache key
            CmdRunner.DeleteDirectoryWithRetry(enlistment.LocalCacheRoot);

            enlistment.MountGVFS();

            // Mount should recreate the local cache root
            enlistment.LocalCacheRoot.ShouldBeADirectory(this.fileSystem);

            // Determine the new local cache key
            string newMappingFileContents = mappingFilePath.ShouldBeAFile(this.fileSystem).WithContents();
            const int GuidStringLength = 32;
            string mappingFileKey = "A {\"Key\":\"https://mseng.visualstudio.com/vsonline/_git/gvfs\",\"Value\":\"";
            int localKeyIndex = newMappingFileContents.IndexOf(mappingFileKey);
            string newCacheKey = newMappingFileContents.Substring(localKeyIndex + mappingFileKey.Length, GuidStringLength);

            // Validate the new objects root is on disk and uses the new key
            objectsRoot.ShouldNotExistOnDisk(this.fileSystem);
            string newObjectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment.DotGVFSRoot);
            newObjectsRoot.ShouldNotEqual(objectsRoot);
            newObjectsRoot.ShouldContain(newCacheKey);
            newObjectsRoot.ShouldBeADirectory(this.fileSystem);

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment);            
        }

        // Override OnTearDownEnlistmentsDeleted rathern than using [TearDown] as the enlistments need to be unmounted before
        // localCacheParentPath can be deleted (as the SQLite blob sizes database cannot be deleted while GVFS is mounted) 
        protected override void OnTearDownEnlistmentsDeleted()
        {
            CmdRunner.DeleteDirectoryWithRetry(this.localCacheParentPath);
        }

        private GVFSFunctionalTestEnlistment CloneAndMountEnlistment(string branch = null)
        {
            return this.CreateNewEnlistment(this.localCachePath, branch);
        }

        private void AlternatesFileShouldHaveGitObjectsRoot(GVFSFunctionalTestEnlistment enlistment)
        {
            string objectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment.DotGVFSRoot);
            string alternatesFileContents = Path.Combine(enlistment.RepoRoot, ".git", "objects", "info", "alternates").ShouldBeAFile(this.fileSystem).WithContents();
            alternatesFileContents.ShouldEqual(objectsRoot);
        }

        private void HydrateRootFolder(GVFSFunctionalTestEnlistment enlistment)
        {
            List<string> allFiles = Directory.EnumerateFiles(enlistment.RepoRoot, "*", SearchOption.TopDirectoryOnly).ToList();
            for (int i = 0; i < allFiles.Count; ++i)
            {
                File.ReadAllText(allFiles[i]);
            }
        }

        private void HydrateEntireRepo(GVFSFunctionalTestEnlistment enlistment)
        {
            List<string> allFiles = Directory.EnumerateFiles(enlistment.RepoRoot, "*", SearchOption.AllDirectories).ToList();
            for (int i = 0; i < allFiles.Count; ++i)
            {
                if (!allFiles[i].StartsWith(enlistment.RepoRoot + "\\.git\\", StringComparison.OrdinalIgnoreCase))
                {
                    File.ReadAllText(allFiles[i]);
                }
            }
        }
    }
}
