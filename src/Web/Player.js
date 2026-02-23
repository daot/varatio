// Player.js
// Handles dynamic video cropping based on .var files

(function () {
  console.log("VARatio script loaded");

  let autoCropEnabled = false;
  let varData = null;
  let currentItemId = null;
  let fetchingItemId = null;
  let observer = null;
  let animationFrameId = null;
  let currentVideoRatio = null;

  // Listen for Jellyfin web client events
  document.addEventListener("viewshow", function (e) {
    if (e.detail && e.detail.type === "video-osd") {
      console.log("VARatio: Video player view shown");
    }
  });

  // We can observe the DOM for the video player and menus
  // Jellyfin uses a singleton class strategy for its video element
  function getVideoElement() {
    return (
      document.querySelector(".htmlVideoplayer") ||
      document.querySelector("video")
    );
  }

  let domCheckTimeout = null;

  function triggerDomCheck() {
    if (domCheckTimeout) return;
    domCheckTimeout = setTimeout(() => {
      domCheckTimeout = null;

      const video = getVideoElement();
      if (video && !video.dataset.varatioAttached) {
        attachToVideo(video);
      }

      const sheets = document.querySelectorAll(".actionSheet");
      for (let i = 0; i < sheets.length; i++) {
        const sheet = sheets[i];
        if (
          sheet.querySelector('[data-id="aspectratio"]') ||
          sheet.querySelector('[data-id="quality"]') ||
          sheet.textContent.includes("Aspect Ratio") ||
          sheet.textContent.includes("Playback Speed")
        ) {
          injectIntoActionSheet(sheet);
        }
      }
    }, 500);
  }

  function initVideoObserver() {
    if (observer) return;

    observer = new MutationObserver((mutations) => {
      let requiresCheck = false;
      for (let i = 0; i < mutations.length; i++) {
        if (mutations[i].addedNodes.length > 0) {
          requiresCheck = true;
          break;
        }
      }
      if (requiresCheck) {
        triggerDomCheck();
      }
    });

    observer.observe(document.body, { childList: true, subtree: true });
  }

  async function fetchVarData(itemId) {
    if (fetchingItemId === itemId) return;
    fetchingItemId = itemId;
    try {
      const headers = {};
      if (
        window.ApiClient &&
        typeof window.ApiClient.accessToken === "function"
      ) {
        headers["Authorization"] =
          'MediaBrowser Token="' + window.ApiClient.accessToken() + '"';
      }
      const response = await fetch("/VARatio/Data?itemId=" + itemId, {
        headers,
      });
      if (response.ok) {
        const text = await response.text();
        varData = parseVarData(text);
        console.log("VARatio: Loaded .var data for item", itemId, varData);
      } else {
        console.log("VARatio: No .var data available for this item.");
        varData = null;
      }
    } catch (e) {
      console.error("VARatio: Failed to fetch .var data", e);
      varData = null;
    } finally {
      fetchingItemId = null;
    }
  }

  function parseVarData(text) {
    const lines = text.split("\n").map((l) => l.trim());
    const data = { segments: [] };

    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      if (line.startsWith("FrameWidth:"))
        data.frameWidth = parseFloat(line.split(":")[1].trim());
      if (line.startsWith("FrameHeight:"))
        data.frameHeight = parseFloat(line.split(":")[1].trim());

      // Check for segment number
      if (/^\d+$/.test(line) && i + 2 < lines.length) {
        const timeStr = lines[i + 1];
        const ratioStr = lines[i + 2];

        if (timeStr.startsWith("Time:")) {
          data.segments.push({
            time: parseFloat(timeStr.split(":")[1].trim()),
            ratio: parseFloat(ratioStr),
          });
          i += 2;
        }
      }
      i++;
    }

    // Sort by time
    data.segments.sort((a, b) => a.time - b.time);
    return data;
  }

  function scheduleNextFrame(video, callback) {
    if ("requestVideoFrameCallback" in video) {
      return video.requestVideoFrameCallback(callback);
    } else {
      return requestAnimationFrame(callback);
    }
  }

  function cancelScheduledFrame(video, id) {
    if ("cancelVideoFrameCallback" in video) {
      video.cancelVideoFrameCallback(id);
    } else {
      cancelAnimationFrame(id);
    }
  }

  function attachToVideo(video) {
    video.dataset.varatioAttached = "true";
    console.log("VARatio: Attached to video");

    // The item ID is usually available globally in Jellyfin, or we can find it
    // A reliable way is to intercept Jellyfin.playbackManager or ApiClient
    // For now, let's try to extract from the URL if it's there
    let itemId = new URLSearchParams(window.location.search).get("id");
    if (
      !itemId &&
      window.ApiClient &&
      window.ApiClient.lastPlaybackProgressOptions
    ) {
      itemId = window.ApiClient.lastPlaybackProgressOptions.ItemId;
    }

    // If not in URL, we wait for playback to start via Jellyfin's state
    if (itemId) {
      currentItemId = itemId;
      fetchVarData(itemId);
    }

    video.addEventListener("play", () => {
      if (!varData && !fetchingItemId) {
        let currentId = new URLSearchParams(window.location.search).get("id");
        if (
          !currentId &&
          window.ApiClient &&
          window.ApiClient.lastPlaybackProgressOptions
        ) {
          currentId = window.ApiClient.lastPlaybackProgressOptions.ItemId;
        }
        if (currentId) {
          currentItemId = currentId;
          fetchVarData(currentId);
        }
      }

      if (autoCropEnabled) {
        startCroppingLoop(video);
      }
    });

    video.addEventListener("pause", () => {
      if (animationFrameId) cancelScheduledFrame(video, animationFrameId);
      animationFrameId = null;
    });

    video.addEventListener("ended", () => {
      if (animationFrameId) cancelScheduledFrame(video, animationFrameId);
      animationFrameId = null;
    });

    // Clean up when video unloads
    const cleanup = () => {
      video.style.transform = "";
      video.style.willChange = "";
      video.dataset.currentRatio = ""; // reset ratio cache
      currentVideoRatio = null;
      varData = null;
      currentItemId = null;
      if (animationFrameId) cancelScheduledFrame(video, animationFrameId);
      animationFrameId = null;
    };
    video.addEventListener("emptied", cleanup);
  }

  function startCroppingLoop(video) {
    if (animationFrameId) {
      cancelScheduledFrame(video, animationFrameId);
      animationFrameId = null;
    }

    // Set will-change to prevent stuttering when transform applies
    if (video.style.willChange !== "transform") {
      video.style.willChange = "transform";
    }

    const loop = (now, metadata) => {
      // Hot path: Minimize GC allocations and DOM access to avoid video skipping
      if (
        varData &&
        varData.segments.length > 0 &&
        autoCropEnabled &&
        !video.paused &&
        !video.ended
      ) {
        // Use exact frame time if available, otherwise fallback to video.currentTime
        const currentTime =
          metadata && typeof metadata.mediaTime === "number"
            ? metadata.mediaTime
            : video.currentTime;

        // Find applicable segment
        // Add 10ms (0.01) epsilon to fix float precision sync issues (ensures we don't apply one frame late)
        let newRatio = varData.segments[0].ratio;
        for (let i = varData.segments.length - 1; i >= 0; i--) {
          if (currentTime + 0.01 >= varData.segments[i].time) {
            newRatio = varData.segments[i].ratio;
            break;
          }
        }

        if (newRatio && newRatio !== currentVideoRatio) {
          applyCrop(video, newRatio);
          currentVideoRatio = newRatio;
          video.dataset.currentRatio = newRatio.toString();
        }
      } else if (autoCropEnabled && !varData && !fetchingItemId) {
        // We defer URLSearchParams out of the hot path by checking only once a second
        if (
          !video.dataset.lastVarCheck ||
          now - parseFloat(video.dataset.lastVarCheck) > 2000
        ) {
          video.dataset.lastVarCheck = now.toString();
          let currentId = new URLSearchParams(window.location.search).get("id");
          if (
            !currentId &&
            window.ApiClient &&
            window.ApiClient.lastPlaybackProgressOptions
          ) {
            currentId = window.ApiClient.lastPlaybackProgressOptions.ItemId;
          }
          if (currentId && currentId !== currentItemId) {
            currentItemId = currentId;
            fetchVarData(currentId);
          }
        }
      }

      if (autoCropEnabled && !video.paused && !video.ended) {
        animationFrameId = scheduleNextFrame(video, loop);
      }
    };
    animationFrameId = scheduleNextFrame(video, loop);
  }

  function applyCrop(video, targetAspectRatio) {
    if (!varData.frameWidth || !varData.frameHeight) return;

    const sourceAspectRatio = varData.frameWidth / varData.frameHeight;

    let scale = 1;
    // If the target aspect ratio is wider than the source, we need to zoom in
    if (targetAspectRatio > sourceAspectRatio) {
      // For example, source is 16:9 (1.77), target is 2.35:1
      // The video file contains black bars top and bottom
      // We need to scale the video up to fill the height, and rely on overflow: hidden to crop the sides

      // Wait, actually, the web player container determines how it's handled.
      // If the video element itself is strictly fitted to the screen (contain):
      // Scaling it up will make it overflow the container, effectively cropping the black bars.
      scale = targetAspectRatio / sourceAspectRatio;
    } else {
      // Target aspect ratio is taller than or equal to source.
      // Say source is 16:9, target is 16:9. Scale is 1.
      scale = 1;
    }

    // Apply instant transition
    video.style.transition = "none";
    video.style.transform = `scale(${scale})`;
  }

  function injectMenuOption() {
    // We will intercept the global player menu when it is rendered
  }

  // We use a single observer now inside initVideoObserver()

  function injectIntoActionSheet(actionSheet) {
    // Check if our option is already there
    if (actionSheet.querySelector(".varatio-option")) return;

    const list = actionSheet.querySelector(".actionSheetScroller");
    if (!list) return;

    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "actionSheetMenuItem emby-button varatio-option";
    btn.innerHTML = `
            <div class="actionSheetItemIcon">
                <span class="material-icons crop_free"></span>
            </div>
            <div class="actionSheetMenuItemText" style="display: flex; align-items: center; justify-content: space-between; width: 100%;">
                <span>VARatio (Auto Crop)</span>
                <span class="material-icons ${autoCropEnabled ? "check" : ""}" style="margin-left: auto;"></span>
            </div>
        `;

    btn.addEventListener("click", () => {
      autoCropEnabled = !autoCropEnabled;
      // Update ui
      const icon = btn.querySelector(".check, .material-icons:last-child");
      if (autoCropEnabled) {
        icon.classList.add("check");
        icon.textContent = "check"; // Depending on jellyfin's icon set
        const video = getVideoElement();
        if (video) startCroppingLoop(video);
      } else {
        icon.classList.remove("check");
        icon.textContent = "";
        const video = getVideoElement();
        if (video) {
          video.style.transform = "";
          video.style.willChange = "";
          video.dataset.currentRatio = ""; // reset ratio
          currentVideoRatio = null;
          if (animationFrameId) cancelScheduledFrame(video, animationFrameId);
          animationFrameId = null;
        }
      }
    });

    // Add it to the list
    list.appendChild(btn);
  }

  // Watch for ItemId change via playbackManager
  // In Jellyfin, there's usually an Events class or window.PlaybackManager we can hook into.
  document.addEventListener("playbackstart", function (e) {
    if (e.detail && e.detail.item) {
      currentItemId = e.detail.item.Id;
      fetchVarData(currentItemId);
    }
  });

  initVideoObserver();
})();
