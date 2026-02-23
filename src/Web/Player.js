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

  function initVideoObserver() {
    if (observer) return;

    observer = new MutationObserver((mutations) => {
      for (let m of mutations) {
        if (m.addedNodes.length) {
          // Check if a new video element was added or if we enter the player
          const video = getVideoElement();
          if (video && !video.dataset.varatioAttached) {
            attachToVideo(video);
          }

          // Check if the OSd is open to inject our menu option
          // Look for the aspect ratio menu
          const aspectRatioMenu = document.querySelector(".aspectRatioMenu"); // just an example, we need to find the correct selector
          if (aspectRatioMenu && !aspectRatioMenu.dataset.varatioAttached) {
            injectMenuOption(aspectRatioMenu);
          }
        }
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
        if (timeStr.includes(":")) {
          data.segments.push({
            time: parseTime(timeStr),
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

  function parseTime(timeStr) {
    // HH:MM:SS.mmm
    const parts = timeStr.split(":");
    if (parts.length === 3) {
      const secParts = parts[2].split(".");
      return (
        parseInt(parts[0]) * 3600 +
        parseInt(parts[1]) * 60 +
        parseInt(secParts[0]) +
        (secParts.length > 1 ? parseInt(secParts[1]) / 1000 : 0)
      );
    }
    return 0;
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
      cancelAnimationFrame(animationFrameId);
    });

    video.addEventListener("ended", () => {
      cancelAnimationFrame(animationFrameId);
    });

    // Clean up when video unloads
    const cleanup = () => {
      video.style.transform = "";
      varData = null;
      currentItemId = null;
      cancelAnimationFrame(animationFrameId);
    };
    video.addEventListener("emptied", cleanup);
  }

  function startCroppingLoop(video) {
    const loop = () => {
      if (autoCropEnabled && !varData && !fetchingItemId) {
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

      if (
        !video.paused &&
        !video.ended &&
        varData &&
        varData.segments.length > 0 &&
        autoCropEnabled
      ) {
        const currentTime = video.currentTime;
        // Find applicable segment
        let currentRatio = null;
        for (let i = varData.segments.length - 1; i >= 0; i--) {
          if (currentTime >= varData.segments[i].time) {
            currentRatio = varData.segments[i].ratio;
            break;
          }
        }

        if (currentRatio) {
          applyCrop(video, currentRatio);
        }
      }
      animationFrameId = requestAnimationFrame(loop);
    };
    animationFrameId = requestAnimationFrame(loop);
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

    // Apply smooth transition
    video.style.transition = "transform 0.5s ease-in-out";
    video.style.transform = `scale(${scale})`;
  }

  function injectMenuOption() {
    // We will intercept the global player menu when it is rendered
  }

  // Fallback: observe DOM for the ActionSheet / Dialog containing video settings to inject our menu item
  new MutationObserver((mutations) => {
    let shouldCheck = false;
    for (const mutation of mutations) {
      if (mutation.addedNodes.length > 0) {
        shouldCheck = true;
        break;
      }
    }
    if (shouldCheck) {
      const sheets = document.querySelectorAll(".actionSheet");
      for (const sheet of sheets) {
        if (
          sheet.querySelector('[data-id="aspectratio"]') ||
          sheet.querySelector('[data-id="quality"]') ||
          sheet.textContent.includes("Aspect Ratio") ||
          sheet.textContent.includes("Playback Speed")
        ) {
          injectIntoActionSheet(sheet);
        }
      }
    }
  }).observe(document.body, { childList: true, subtree: true });

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
          cancelAnimationFrame(animationFrameId);
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
