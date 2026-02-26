// Player.js
// VARatio: Server-side cropped stream integration for Jellyfin
//
// When enabled, replaces the video source with a custom FFmpeg-transcoded stream
// that has aspect ratio cropping baked in at the encoder level.

(function () {
  console.log("VARatio script loaded");

  let varatioCropEnabled = false;
  let currentItemId = null;
  let hasVarData = false;
  let checkingVarData = false;
  let observer = null;
  let originalSrc = null;

  // ── DOM helpers ────────────────────────────────────────────────────────

  function getVideoElement() {
    return (
      document.querySelector(".htmlVideoplayer") ||
      document.querySelector("video")
    );
  }

  function getAuthHeaders() {
    const headers = {};
    if (
      window.ApiClient &&
      typeof window.ApiClient.accessToken === "function"
    ) {
      headers["Authorization"] =
        'MediaBrowser Token="' + window.ApiClient.accessToken() + '"';
    }
    return headers;
  }

  function getAccessToken() {
    if (
      window.ApiClient &&
      typeof window.ApiClient.accessToken === "function"
    ) {
      return window.ApiClient.accessToken();
    }
    return null;
  }

  function getCurrentItemId() {
    let itemId = new URLSearchParams(window.location.search).get("id");
    if (
      !itemId &&
      window.ApiClient &&
      window.ApiClient.lastPlaybackProgressOptions
    ) {
      itemId = window.ApiClient.lastPlaybackProgressOptions.ItemId;
    }
    return itemId;
  }

  // ── VARatio data check ────────────────────────────────────────────────

  async function checkVarData(itemId) {
    if (checkingVarData || !itemId) return;
    checkingVarData = true;
    currentItemId = itemId;

    try {
      const response = await fetch("/VARatio/Data?itemId=" + itemId, {
        headers: getAuthHeaders(),
      });
      hasVarData = response.ok;
      console.log(
        "VARatio: .var data " +
          (hasVarData ? "available" : "not found") +
          " for item " +
          itemId,
      );
    } catch (e) {
      console.error("VARatio: Failed to check .var data", e);
      hasVarData = false;
    } finally {
      checkingVarData = false;
    }
  }

  // ── Stream source swapping ────────────────────────────────────────────

  function buildStreamUrl(itemId) {
    var url = "/VARatio/Stream?itemId=" + itemId;
    var token = getAccessToken();
    if (token) {
      url += "&api_key=" + encodeURIComponent(token);
    }
    return url;
  }

  function enableVaratioCrop() {
    var video = getVideoElement();
    if (!video || !currentItemId || !hasVarData) {
      console.warn(
        "VARatio: Cannot enable — missing video, item ID, or .var data",
      );
      return;
    }

    // Save the original source so we can restore it later
    if (!originalSrc) {
      originalSrc = video.src || video.currentSrc;
    }

    var currentTime = video.currentTime || 0;
    var startTimeTicks = Math.round(currentTime * 10000000);
    var streamUrl = buildStreamUrl(currentItemId);
    if (startTimeTicks > 0) {
      streamUrl += "&startTimeTicks=" + startTimeTicks;
    }

    console.log("VARatio: Switching to cropped stream:", streamUrl);

    // Swap the video source
    video.src = streamUrl;
    video.load();
    video.play().catch(function (e) {
      console.warn("VARatio: Autoplay failed:", e);
    });

    varatioCropEnabled = true;
  }

  function disableVaratioCrop() {
    var video = getVideoElement();
    if (!video) return;

    if (originalSrc) {
      console.log("VARatio: Restoring original stream");
      var currentTime = video.currentTime || 0;
      video.src = originalSrc;
      video.load();
      video.currentTime = currentTime;
      video.play().catch(function (e) {
        console.warn("VARatio: Autoplay failed on restore:", e);
      });
      originalSrc = null;
    }

    varatioCropEnabled = false;
  }

  // ── DOM observer ──────────────────────────────────────────────────────

  let domCheckTimeout = null;

  function triggerDomCheck() {
    if (domCheckTimeout) return;
    domCheckTimeout = setTimeout(function () {
      domCheckTimeout = null;

      // Check for video element and fetch var data
      var video = getVideoElement();
      if (video && !video.dataset.varatioAttached) {
        attachToVideo(video);
      }

      // Check for action sheets to inject our toggle
      var sheets = document.querySelectorAll(".actionSheet");
      for (var i = 0; i < sheets.length; i++) {
        var sheet = sheets[i];
        if (
          sheet.querySelector('[data-id="aspectratio"]') ||
          sheet.querySelector('[data-id="quality"]') ||
          sheet.textContent.indexOf("Aspect Ratio") !== -1 ||
          sheet.textContent.indexOf("Playback Speed") !== -1
        ) {
          injectIntoActionSheet(sheet);
        }
      }
    }, 500);
  }

  function initVideoObserver() {
    if (observer) return;

    observer = new MutationObserver(function (mutations) {
      for (var i = 0; i < mutations.length; i++) {
        if (mutations[i].addedNodes.length > 0) {
          triggerDomCheck();
          break;
        }
      }
    });

    observer.observe(document.body, { childList: true, subtree: true });
  }

  // ── Video attachment ──────────────────────────────────────────────────

  function attachToVideo(video) {
    video.dataset.varatioAttached = "true";
    console.log("VARatio: Attached to video");

    // Try to get item ID and check for .var data
    var itemId = getCurrentItemId();
    if (itemId) {
      checkVarData(itemId);
    }

    video.addEventListener("play", function () {
      if (!hasVarData && !checkingVarData) {
        var currentId = getCurrentItemId();
        if (currentId && currentId !== currentItemId) {
          checkVarData(currentId);
        }
      }
    });

    // Clean up state when video unloads
    video.addEventListener("emptied", function () {
      hasVarData = false;
      currentItemId = null;
      originalSrc = null;
      varatioCropEnabled = false;
    });
  }

  // ── Action sheet toggle ───────────────────────────────────────────────

  function injectIntoActionSheet(actionSheet) {
    if (actionSheet.querySelector(".varatio-option")) return;

    var list = actionSheet.querySelector(".actionSheetScroller");
    if (!list) return;

    var btn = document.createElement("button");
    btn.type = "button";
    btn.className = "actionSheetMenuItem emby-button varatio-option";
    btn.innerHTML =
      '<div class="actionSheetItemIcon">' +
      '<span class="material-icons crop_free"></span>' +
      "</div>" +
      '<div class="actionSheetMenuItemText" style="display: flex; align-items: center; justify-content: space-between; width: 100%;">' +
      "<span>VARatio (Auto Crop)</span>" +
      '<span class="material-icons ' +
      (varatioCropEnabled ? "check" : "") +
      '" style="margin-left: auto;"></span>' +
      "</div>";

    btn.addEventListener("click", function () {
      if (!varatioCropEnabled) {
        enableVaratioCrop();
      } else {
        disableVaratioCrop();
      }

      // Update the icon
      var icon = btn.querySelector(".material-icons:last-child");
      if (varatioCropEnabled) {
        icon.classList.add("check");
        icon.textContent = "check";
      } else {
        icon.classList.remove("check");
        icon.textContent = "";
      }
    });

    list.appendChild(btn);
  }

  // ── Event hooks ───────────────────────────────────────────────────────

  document.addEventListener("playbackstart", function (e) {
    if (e.detail && e.detail.item) {
      currentItemId = e.detail.item.Id;
      checkVarData(currentItemId);
    }
  });

  document.addEventListener("viewshow", function (e) {
    if (e.detail && e.detail.type === "video-osd") {
      console.log("VARatio: Video player view shown");
    }
  });

  initVideoObserver();
})();
