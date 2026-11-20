// @module: reStation.Core.Gpu.VulkanBindings
// @purpose: Clean-room dynamic P/Invoke loader and Vulkan 1.x C-ABI structs/constants for hardware R16UI VRAM backing (Windows/Linux/macOS).
// @depends-on: System, System.Runtime.InteropServices, System.Text
// @exports: VulkanBindings, VulkanConst
// @owner: devv-gpu-backends (T3)

using System.Runtime.InteropServices;
using System.Text;

namespace reStation.Core.Gpu;

internal static class VulkanConst
{
    public const int VkSuccess = 0, VkErrorIncompatibleDriver = -9;
    public const uint VkFormatR16Uint = 74u;
    public const uint VkImageType2D = 1u, VkImageViewType2D = 1u;
    public const uint VkImageUsageTransferSrcBit = 0x01u, VkImageUsageTransferDstBit = 0x02u, VkImageUsageSampledBit = 0x04u;
    public const uint VkBufferUsageTransferSrcBit = 0x01u, VkBufferUsageTransferDstBit = 0x02u;
    public const uint VkMemoryPropertyDeviceLocalBit = 0x01u, VkMemoryPropertyHostVisibleBit = 0x02u, VkMemoryPropertyHostCoherentBit = 0x04u;
    public const uint VkQueueGraphicsBit = 0x01u, VkQueueTransferBit = 0x04u;
    public const uint VkImageLayoutUndefined = 0u, VkImageLayoutGeneral = 1u, VkImageLayoutTransferSrcOptimal = 6u, VkImageLayoutTransferDstOptimal = 7u;
    public const uint VkAccessTransferReadBit = 0x0800u, VkAccessTransferWriteBit = 0x1000u, VkAccessMemoryReadBit = 0x8000u;
    public const uint VkPipelineStageTopOfPipeBit = 0x01u, VkPipelineStageTransferBit = 0x1000u, VkPipelineStageBottomOfPipeBit = 0x2000u;
    public const uint VkFenceCreateSignaledBit = 1u, VkCommandPoolCreateResetBit = 2u, VkCommandBufferOneTimeSubmitBit = 1u;
    public const uint VkQueueFamilyIgnored = ~0u;
    public const uint VkFormatFeatureSampledImageBit = 0x01u;
    public static uint MakeApiVersion(uint major, uint minor, uint patch) => (major << 22) | (minor << 12) | patch;
}

[StructLayout(LayoutKind.Sequential)] internal unsafe struct VkAppInfo { public uint SType; public IntPtr PNext; public byte* PAppName; public uint AppVersion; public byte* PEngineName; public uint EngineVersion, ApiVersion; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct VkInstCreateInfo { public uint SType; public IntPtr PNext; public uint Flags; public VkAppInfo* PAppInfo; public uint EnabledLayerCount; public byte** PpEnabledLayerNames; public uint EnabledExtensionCount; public byte** PpEnabledExtensionNames; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct VkDevQueueCreateInfo { public uint SType; public IntPtr PNext; public uint Flags, QueueFamilyIndex, QueueCount; public float* PQueuePriorities; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct VkDevCreateInfo { public uint SType; public IntPtr PNext; public uint Flags, QueueCreateInfoCount; public VkDevQueueCreateInfo* PQueueCreateInfos; public uint EnabledLayerCount; public byte** PpEnabledLayerNames; public uint EnabledExtensionCount; public byte** PpEnabledExtensionNames; public IntPtr PEnabledFeatures; }
[StructLayout(LayoutKind.Sequential)] internal struct VkQFamilyProps { public uint QueueFlags, QueueCount, TimestampValidBits, GranX, GranY, GranZ; }
[StructLayout(LayoutKind.Sequential)] internal struct VkFmtProps { public uint LinearTilingFeatures, OptimalTilingFeatures, BufferFeatures; }
[StructLayout(LayoutKind.Sequential)] internal struct VkMemReqs { public ulong Size, Alignment; public uint MemoryTypeBits; private uint _pad; }
[StructLayout(LayoutKind.Sequential)] internal struct VkMemAllocInfo { public uint SType; public IntPtr PNext; public ulong AllocationSize; public uint MemoryTypeIndex; private uint _pad; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct VkPhysMemProps { public uint MemoryTypeCount; public fixed uint MemoryTypes[64]; public uint MemoryHeapCount; public fixed ulong MemoryHeaps[32]; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct VkBufCreateInfo { public uint SType; public IntPtr PNext; public uint Flags; public ulong Size; public uint Usage, SharingMode, QueueFamilyIndexCount; public uint* PQueueFamilyIndices; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct VkImgCreateInfo { public uint SType; public IntPtr PNext; public uint Flags, ImageType, Format, ExtentW, ExtentH, ExtentD, MipLevels, ArrayLayers, Samples, Tiling, Usage, SharingMode, QueueFamilyIndexCount; public uint* PQueueFamilyIndices; public uint InitialLayout; }
[StructLayout(LayoutKind.Sequential)] internal struct VkImgSubresRange { public uint AspectMask, BaseMipLevel, LevelCount, BaseArrayLayer, LayerCount; }
[StructLayout(LayoutKind.Sequential)] internal struct VkImgViewCreateInfo { public uint SType; public IntPtr PNext; public uint Flags; public ulong Image; public uint ViewType, Format, CompR, CompG, CompB, CompA; public VkImgSubresRange SubresourceRange; }
[StructLayout(LayoutKind.Sequential)] internal struct VkCmdPoolCreateInfo { public uint SType; public IntPtr PNext; public uint Flags, QueueFamilyIndex; }
[StructLayout(LayoutKind.Sequential)] internal struct VkCmdBufAllocInfo { public uint SType; public IntPtr PNext; public ulong CommandPool; public uint Level, CommandBufferCount; }
[StructLayout(LayoutKind.Sequential)] internal struct VkCmdBufBeginInfo { public uint SType; public IntPtr PNext; public uint Flags; public IntPtr PInheritanceInfo; }
[StructLayout(LayoutKind.Sequential)] internal struct VkFenceCreateInfo { public uint SType; public IntPtr PNext; public uint Flags; }
[StructLayout(LayoutKind.Sequential)] internal struct VkImgMemBarrier { public uint SType; public IntPtr PNext; public uint SrcAccessMask, DstAccessMask, OldLayout, NewLayout, SrcQueueFamilyIndex, DstQueueFamilyIndex; public ulong Image; public VkImgSubresRange SubresourceRange; }
[StructLayout(LayoutKind.Sequential)] internal struct VkImgSubresLayers { public uint AspectMask, MipLevel, BaseArrayLayer, LayerCount; }
[StructLayout(LayoutKind.Sequential)] internal struct VkBufImgCopy { public ulong BufferOffset; public uint BufferRowLength, BufferImageHeight; public VkImgSubresLayers ImageSubresource; public int OffsetX, OffsetY, OffsetZ; public uint ExtentWidth, ExtentHeight, ExtentDepth; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct VkSubmitInfo { public uint SType; public IntPtr PNext; public uint WaitSemaphoreCount; public ulong* PWaitSemaphores; public uint* PWaitDstStageMask; public uint CommandBufferCount; public IntPtr* PCommandBuffers; public uint SignalSemaphoreCount; public ulong* PSignalSemaphores; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPhysDevPropsBuffer
{
    public fixed byte Raw[1024];

    public uint ApiVersion
    {
        get { fixed (byte* p = Raw) return *(uint*)p; }
    }

    public uint DeviceType
    {
        get { fixed (byte* p = Raw) return *(uint*)(p + 16); }
    }

    public string ReadDeviceName()
    {
        fixed (byte* p = Raw)
        {
            byte* namePtr = p + 20;
            int len = 0;
            while (len < 255 && namePtr[len] != 0) len++;
            return Encoding.UTF8.GetString(namePtr, len);
        }
    }
}

internal sealed unsafe class VulkanBindings : IDisposable
{
    private IntPtr _libHandle;

    internal delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr> GetInstanceProcAddr;
    internal delegate* unmanaged[Stdcall]<VkInstCreateInfo*, IntPtr, IntPtr*, int> CreateInstance;
    internal delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void> DestroyInstance;
    internal delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr*, int> EnumeratePhysicalDevices;
    internal delegate* unmanaged[Stdcall]<IntPtr, VkPhysDevPropsBuffer*, void> GetPhysicalDeviceProperties;
    internal delegate* unmanaged[Stdcall]<IntPtr, uint*, VkQFamilyProps*, void> GetPhysicalDeviceQueueFamilyProperties;
    internal delegate* unmanaged[Stdcall]<IntPtr, VkPhysMemProps*, void> GetPhysicalDeviceMemoryProperties;
    internal delegate* unmanaged[Stdcall]<IntPtr, uint, VkFmtProps*, void> GetPhysicalDeviceFormatProperties;
    internal delegate* unmanaged[Stdcall]<IntPtr, VkDevCreateInfo*, IntPtr, IntPtr*, int> CreateDevice;
    internal delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr> GetDeviceProcAddr;

    internal delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void> DestroyDevice;
    internal delegate* unmanaged[Stdcall]<IntPtr, int> DeviceWaitIdle;
    internal delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void> GetDeviceQueue;
    internal delegate* unmanaged[Stdcall]<IntPtr, VkCmdPoolCreateInfo*, IntPtr, ulong*, int> CreateCommandPool;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void> DestroyCommandPool;
    internal delegate* unmanaged[Stdcall]<IntPtr, VkCmdBufAllocInfo*, IntPtr*, int> AllocateCommandBuffers;
    internal delegate* unmanaged[Stdcall]<IntPtr, uint, int> ResetCommandBuffer;
    internal delegate* unmanaged[Stdcall]<IntPtr, VkCmdBufBeginInfo*, int> BeginCommandBuffer;
    internal delegate* unmanaged[Stdcall]<IntPtr, int> EndCommandBuffer;
    internal delegate* unmanaged[Stdcall]<IntPtr, VkFenceCreateInfo*, IntPtr, ulong*, int> CreateFence;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void> DestroyFence;
    internal delegate* unmanaged[Stdcall]<IntPtr, uint, ulong*, uint, ulong, int> WaitForFences;
    internal delegate* unmanaged[Stdcall]<IntPtr, uint, ulong*, int> ResetFences;
    internal delegate* unmanaged[Stdcall]<IntPtr, uint, VkSubmitInfo*, ulong, int> QueueSubmit;
    internal delegate* unmanaged[Stdcall]<IntPtr, VkBufCreateInfo*, IntPtr, ulong*, int> CreateBuffer;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void> DestroyBuffer;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, VkMemReqs*, void> GetBufferMemoryRequirements;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, int> BindBufferMemory;
    internal delegate* unmanaged[Stdcall]<IntPtr, VkImgCreateInfo*, IntPtr, ulong*, int> CreateImage;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void> DestroyImage;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, VkMemReqs*, void> GetImageMemoryRequirements;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, int> BindImageMemory;
    internal delegate* unmanaged[Stdcall]<IntPtr, VkImgViewCreateInfo*, IntPtr, ulong*, int> CreateImageView;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void> DestroyImageView;
    internal delegate* unmanaged[Stdcall]<IntPtr, VkMemAllocInfo*, IntPtr, ulong*, int> AllocateMemory;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void> FreeMemory;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, uint, void**, int> MapMemory;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, void> UnmapMemory;
    internal delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, IntPtr, uint, IntPtr, uint, VkImgMemBarrier*, void> CmdPipelineBarrier;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, uint, uint, VkBufImgCopy*, void> CmdCopyBufferToImage;
    internal delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, ulong, uint, VkBufImgCopy*, void> CmdCopyImageToBuffer;

    public string LoadedLibraryName { get; private set; } = string.Empty;

    public static IReadOnlyList<string> DefaultLibraryCandidates =>
        OperatingSystem.IsWindows()
            ? ["vulkan-1.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libvulkan.1.dylib", "libMoltenVK.dylib", "libvulkan.dylib"]
                : ["libvulkan.so.1", "libvulkan.so"];

    public static bool TryLoad(out VulkanBindings? bindings, out string diagnostic, IReadOnlyList<string>? libraryCandidates = null)
    {
        bindings = null;
        IReadOnlyList<string> candidates = libraryCandidates ?? DefaultLibraryCandidates;
        foreach (string lib in candidates)
        {
            if (string.IsNullOrWhiteSpace(lib) || !NativeLibrary.TryLoad(lib, out IntPtr handle)) continue;
            if (!NativeLibrary.TryGetExport(handle, "vkGetInstanceProcAddr", out IntPtr gipaPtr) || gipaPtr == IntPtr.Zero)
            {
                NativeLibrary.Free(handle);
                continue;
            }

            var vk = new VulkanBindings { _libHandle = handle, LoadedLibraryName = lib };
            vk.GetInstanceProcAddr = (delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr>)gipaPtr;
            vk.CreateInstance = (delegate* unmanaged[Stdcall]<VkInstCreateInfo*, IntPtr, IntPtr*, int>)vk.GetInst(IntPtr.Zero, "vkCreateInstance");
            if (vk.CreateInstance == null)
            {
                vk.Dispose();
                continue;
            }

            bindings = vk;
            diagnostic = $"Loaded Vulkan loader '{lib}'.";
            return true;
        }

        diagnostic = $"Vulkan ICD loader unavailable (tried: {string.Join(", ", candidates)}).";
        return false;
    }

    public bool LoadInstanceProcs(IntPtr instance)
    {
        DestroyInstance = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)GetInst(instance, "vkDestroyInstance");
        EnumeratePhysicalDevices = (delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr*, int>)GetInst(instance, "vkEnumeratePhysicalDevices");
        GetPhysicalDeviceProperties = (delegate* unmanaged[Stdcall]<IntPtr, VkPhysDevPropsBuffer*, void>)GetInst(instance, "vkGetPhysicalDeviceProperties");
        GetPhysicalDeviceQueueFamilyProperties = (delegate* unmanaged[Stdcall]<IntPtr, uint*, VkQFamilyProps*, void>)GetInst(instance, "vkGetPhysicalDeviceQueueFamilyProperties");
        GetPhysicalDeviceMemoryProperties = (delegate* unmanaged[Stdcall]<IntPtr, VkPhysMemProps*, void>)GetInst(instance, "vkGetPhysicalDeviceMemoryProperties");
        GetPhysicalDeviceFormatProperties = (delegate* unmanaged[Stdcall]<IntPtr, uint, VkFmtProps*, void>)GetInst(instance, "vkGetPhysicalDeviceFormatProperties");
        CreateDevice = (delegate* unmanaged[Stdcall]<IntPtr, VkDevCreateInfo*, IntPtr, IntPtr*, int>)GetInst(instance, "vkCreateDevice");
        GetDeviceProcAddr = (delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr>)GetInst(instance, "vkGetDeviceProcAddr");
        return DestroyInstance != null && EnumeratePhysicalDevices != null &&
               GetPhysicalDeviceProperties != null && GetPhysicalDeviceQueueFamilyProperties != null &&
               GetPhysicalDeviceMemoryProperties != null && CreateDevice != null && GetDeviceProcAddr != null;
    }

    public bool LoadDeviceProcs(IntPtr device)
    {
        DestroyDevice = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)GetDev(device, "vkDestroyDevice");
        DeviceWaitIdle = (delegate* unmanaged[Stdcall]<IntPtr, int>)GetDev(device, "vkDeviceWaitIdle");
        GetDeviceQueue = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)GetDev(device, "vkGetDeviceQueue");
        CreateCommandPool = (delegate* unmanaged[Stdcall]<IntPtr, VkCmdPoolCreateInfo*, IntPtr, ulong*, int>)GetDev(device, "vkCreateCommandPool");
        DestroyCommandPool = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void>)GetDev(device, "vkDestroyCommandPool");
        AllocateCommandBuffers = (delegate* unmanaged[Stdcall]<IntPtr, VkCmdBufAllocInfo*, IntPtr*, int>)GetDev(device, "vkAllocateCommandBuffers");
        ResetCommandBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)GetDev(device, "vkResetCommandBuffer");
        BeginCommandBuffer = (delegate* unmanaged[Stdcall]<IntPtr, VkCmdBufBeginInfo*, int>)GetDev(device, "vkBeginCommandBuffer");
        EndCommandBuffer = (delegate* unmanaged[Stdcall]<IntPtr, int>)GetDev(device, "vkEndCommandBuffer");
        CreateFence = (delegate* unmanaged[Stdcall]<IntPtr, VkFenceCreateInfo*, IntPtr, ulong*, int>)GetDev(device, "vkCreateFence");
        DestroyFence = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void>)GetDev(device, "vkDestroyFence");
        WaitForFences = (delegate* unmanaged[Stdcall]<IntPtr, uint, ulong*, uint, ulong, int>)GetDev(device, "vkWaitForFences");
        ResetFences = (delegate* unmanaged[Stdcall]<IntPtr, uint, ulong*, int>)GetDev(device, "vkResetFences");
        QueueSubmit = (delegate* unmanaged[Stdcall]<IntPtr, uint, VkSubmitInfo*, ulong, int>)GetDev(device, "vkQueueSubmit");
        CreateBuffer = (delegate* unmanaged[Stdcall]<IntPtr, VkBufCreateInfo*, IntPtr, ulong*, int>)GetDev(device, "vkCreateBuffer");
        DestroyBuffer = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void>)GetDev(device, "vkDestroyBuffer");
        GetBufferMemoryRequirements = (delegate* unmanaged[Stdcall]<IntPtr, ulong, VkMemReqs*, void>)GetDev(device, "vkGetBufferMemoryRequirements");
        BindBufferMemory = (delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, int>)GetDev(device, "vkBindBufferMemory");
        CreateImage = (delegate* unmanaged[Stdcall]<IntPtr, VkImgCreateInfo*, IntPtr, ulong*, int>)GetDev(device, "vkCreateImage");
        DestroyImage = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void>)GetDev(device, "vkDestroyImage");
        GetImageMemoryRequirements = (delegate* unmanaged[Stdcall]<IntPtr, ulong, VkMemReqs*, void>)GetDev(device, "vkGetImageMemoryRequirements");
        BindImageMemory = (delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, int>)GetDev(device, "vkBindImageMemory");
        CreateImageView = (delegate* unmanaged[Stdcall]<IntPtr, VkImgViewCreateInfo*, IntPtr, ulong*, int>)GetDev(device, "vkCreateImageView");
        DestroyImageView = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void>)GetDev(device, "vkDestroyImageView");
        AllocateMemory = (delegate* unmanaged[Stdcall]<IntPtr, VkMemAllocInfo*, IntPtr, ulong*, int>)GetDev(device, "vkAllocateMemory");
        FreeMemory = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, void>)GetDev(device, "vkFreeMemory");
        MapMemory = (delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, ulong, uint, void**, int>)GetDev(device, "vkMapMemory");
        UnmapMemory = (delegate* unmanaged[Stdcall]<IntPtr, ulong, void>)GetDev(device, "vkUnmapMemory");
        CmdPipelineBarrier = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, IntPtr, uint, IntPtr, uint, VkImgMemBarrier*, void>)GetDev(device, "vkCmdPipelineBarrier");
        CmdCopyBufferToImage = (delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, uint, uint, VkBufImgCopy*, void>)GetDev(device, "vkCmdCopyBufferToImage");
        CmdCopyImageToBuffer = (delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, ulong, uint, VkBufImgCopy*, void>)GetDev(device, "vkCmdCopyImageToBuffer");
        return DestroyDevice != null && DeviceWaitIdle != null && GetDeviceQueue != null &&
               CreateCommandPool != null && AllocateCommandBuffers != null && CreateFence != null &&
               QueueSubmit != null && CreateBuffer != null && CreateImage != null &&
               AllocateMemory != null && MapMemory != null && CmdCopyBufferToImage != null && CmdCopyImageToBuffer != null;
    }

    private IntPtr GetInst(IntPtr inst, string name) { byte[] b = Encoding.ASCII.GetBytes(name + "\0"); fixed (byte* p = b) return GetInstanceProcAddr(inst, p); }
    private IntPtr GetDev(IntPtr dev, string name) { byte[] b = Encoding.ASCII.GetBytes(name + "\0"); fixed (byte* p = b) return GetDeviceProcAddr(dev, p); }

    public void Dispose()
    {
        if (_libHandle != IntPtr.Zero) { NativeLibrary.Free(_libHandle); _libHandle = IntPtr.Zero; }
    }
}
