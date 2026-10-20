const $ = (id) => document.getElementById(id);
const invoke = (name, ...args) => DotNet.invokeMethodAsync("PclPatcher.Web", name, ...args);
const maxSize = 128 * 1024 * 1024;
let ready = false, busy = false, selected = null, inspection = null, result = null, downloadUrl = null;
const fileInput = $("file");
const formatSize = (n) => n < 1024 * 1024 ? `${(n / 1024).toFixed(1)} KB` : `${(n / (1024 * 1024)).toFixed(1)} MB`;
const nextFrame = () => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
function announce(text) { $("announcement").textContent = text; }
function setBusy(value) {
  busy = value;
  fileInput.disabled = value || !ready;
  $("file-label").classList.toggle("disabled", fileInput.disabled);
  $("reset").disabled = value;
  $("action").disabled = value || !inspection;
  $("patch-panel").setAttribute("aria-busy", String(value));
}
function showError(error) {
  const message = String(error?.message ?? error).split("\n")[0].replace(/^.*(?:CompatibilityException|InvalidOperationException):\s*/, "");
  $("error-message").textContent = message;
  $("error").hidden = false;
  announce(message);
}
async function clearFile() {
  if (busy) return;
  if (downloadUrl) URL.revokeObjectURL(downloadUrl);
  downloadUrl = null; selected = null; inspection = null; result = null;
  if (ready) await invoke("Reset");
  fileInput.value = "";
  $("file-title").textContent = "Drop your PCL2 executable here";
  $("file-subtitle").textContent = "Original .exe file · up to 128 MB";
  $("browse-text").textContent = "Browse files";
  $("action-text").textContent = "Select a file to continue";
  $("button-note").textContent = "A new file is created. Your original stays untouched.";
  ["error", "file-info", "success", "reset", "report"].forEach(id => $(id).hidden = true);
  $("log").textContent = "";
  $("report").open = false;
  setBusy(false);
}
async function chooseFile(file) {
  if (busy || !ready || !file) return;
  await clearFile();
  $("reset").hidden = false;
  if (!/\.exe$/i.test(file.name)) { showError("Choose an .exe file, not a ZIP archive or installer package."); return; }
  if (file.size > maxSize) { showError("This file exceeds the 128 MB limit."); return; }
  if (file.size === 0) { showError("This file is empty. Choose an original PCL2 executable."); return; }
  selected = file;
  $("file-title").textContent = file.name;
  $("file-subtitle").textContent = `${formatSize(file.size)} · Checking compatibility…`;
  $("browse-text").textContent = "Change file";
  $("action-text").textContent = "Inspecting executable…";
  setBusy(true); announce("Inspecting the selected executable on your device.");
  await nextFrame();
  try {
    const bytes = new Uint8Array(await file.arrayBuffer());
    inspection = await invoke("Inspect", bytes);
    $("file-subtitle").textContent = `${formatSize(file.size)} · Compatible PCL2 executable`;
    $("assembly").textContent = inspection.assembly;
    $("version").textContent = inspection.version;
    $("input-hash").textContent = inspection.sha256;
    $("file-info").hidden = false;
    $("action-text").textContent = "Patch executable";
    announce("Compatible PCL2 executable. Ready to patch.");
  } catch (error) {
    inspection = null;
    $("file-subtitle").textContent = `${formatSize(file.size)} · Not compatible`;
    $("action-text").textContent = "Choose a compatible file";
    showError(error);
  } finally { setBusy(false); }
}
fileInput.addEventListener("change", () => chooseFile(fileInput.files[0]));
const zone = $("dropzone");
let dragDepth = 0;
zone.addEventListener("dragenter", (event) => { event.preventDefault(); if (ready && !busy) { dragDepth++; zone.classList.add("dragging"); } });
zone.addEventListener("dragover", (event) => { event.preventDefault(); event.dataTransfer.dropEffect = ready && !busy ? "copy" : "none"; });
zone.addEventListener("dragleave", (event) => { event.preventDefault(); if (--dragDepth <= 0) zone.classList.remove("dragging"); });
zone.addEventListener("drop", (event) => {
  event.preventDefault(); dragDepth = 0; zone.classList.remove("dragging");
  if (busy || !ready) return;
  if (event.dataTransfer.files.length !== 1) { showError("Select one executable at a time."); return; }
  chooseFile(event.dataTransfer.files[0]);
});
window.addEventListener("dragover", e => e.preventDefault());
window.addEventListener("drop", e => e.preventDefault());
$("reset").addEventListener("click", clearFile);
$("action").addEventListener("click", async () => {
  if (busy || !inspection) return;
  if (result && downloadUrl) {
    const link = document.createElement("a");
    link.href = downloadUrl; link.download = selected.name.replace(/\.exe$/i, ".patched.exe");
    document.body.append(link); link.click(); link.remove();
    announce("Download started. Keep your original executable as a backup.");
    return;
  }
  setBusy(true); $("error").hidden = true;
  $("action-text").textContent = "Patching and verifying…";
  announce("Applying five patches and checking the output.");
  await nextFrame();
  try {
    result = await invoke("Patch");
    const bytes = await invoke("Download");
    downloadUrl = URL.createObjectURL(new Blob([bytes], { type: "application/octet-stream" }));
    $("log").textContent = result.log.join("\n") + `\n\nOriginal SHA-256\n${inspection.sha256}\n\nPatched SHA-256\n${result.sha256}\n\nOutput size: ${formatSize(result.size)}`;
    $("report").hidden = false;
    $("success").hidden = false;
    $("action-text").textContent = "Download patched executable";
    $("button-note").textContent = `${formatSize(result.size)} · Save alongside your original as a backup.`;
    $("file-subtitle").textContent = "All 5 patches applied · Output verified";
    announce("Patching complete. Your verified executable is ready to download.");
  } catch (error) {
    result = null; inspection = null;
    await invoke("Reset");
    $("action-text").textContent = "Choose another executable";
    showError(error);
  } finally { setBusy(false); }
});
window.addEventListener("pagehide", () => { if (downloadUrl) URL.revokeObjectURL(downloadUrl); });
async function startRuntime() {
  let timeout;
  // Blazor.start can resolve before the managed entry point has loaded.
  const managedReady = new Promise(resolve => { window.pclRuntimeReady = resolve; });
  try {
    await Blazor.start();
    await Promise.race([managedReady, new Promise((_, reject) => {
      timeout = setTimeout(() => reject(new Error("Engine startup timed out")), 45000);
    })]);
    // Verify the interop entry points survived trimming before enabling file selection.
    await invoke("Reset");
    ready = true; setBusy(false);
    $("runtime").dataset.state = "ready";
    $("runtime-text").textContent = "Ready";
    $("browse-text").textContent = "Browse files";
    announce("Ready. Choose your PCL2 executable.");
  } catch (error) {
    $("runtime").dataset.state = "error";
    $("runtime-text").textContent = "Engine couldn't load";
    $("browse-text").textContent = "Engine unavailable";
    showError("The WebAssembly engine couldn't load. Refresh the page to retry, and check that your browser supports WebAssembly.");
    console.error("Runtime initialization failed", error);
  } finally { clearTimeout(timeout); delete window.pclRuntimeReady; }
}
startRuntime();


