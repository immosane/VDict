namespace VDict.Core.Text;

public static class ReaderBrowserDiagnostics
{
    public const string FrameProbeScript =
        """
        JSON.stringify((() => {
          const htmlStyle = getComputedStyle(document.documentElement);
          const bodyStyle = getComputedStyle(document.body);
          const mainElement = document.body;
          const mainStyle = mainElement ? getComputedStyle(mainElement) : null;
          const probeX = innerWidth / 2;
          const probeY = innerHeight / 2;
          const hitElement = document.elementFromPoint(probeX, probeY);
          const overlays = Array.from(document.querySelectorAll("*")).filter(element => {
            const style = getComputedStyle(element);
            const rect = element.getBoundingClientRect();
            return (style.position === "fixed" || style.position === "absolute") &&
              rect.width >= innerWidth * 0.8 && rect.height >= innerHeight * 0.8;
          }).slice(0, 10).map(element => ({
            tag: element.tagName,
            id: element.id,
            className: String(element.className).slice(0, 200),
            pointerEvents: getComputedStyle(element).pointerEvents,
            zIndex: getComputedStyle(element).zIndex
          }));
          const timingFor = name => performance.getEntriesByName(name).map(entry => ({
            name: entry.name,
            initiatorType: entry.initiatorType || "",
            duration: entry.duration,
            transferSize: entry.transferSize || 0,
            encodedBodySize: entry.encodedBodySize || 0,
            decodedBodySize: entry.decodedBodySize || 0,
            responseStatus: entry.responseStatus || 0
          }));
          const images = Array.from(document.images).map(image => ({
            originalSrc: image.getAttribute("data-vdict-original-src") ||
              image.getAttribute("src") || "",
            rewrittenSrc: image.getAttribute("src") || "",
            currentSrc: image.currentSrc || "",
            complete: image.complete,
            naturalWidth: image.naturalWidth,
            naturalHeight: image.naturalHeight,
            performanceEntries: timingFor(image.currentSrc || image.src)
          }));
          const backgroundResources = Array.from(document.querySelectorAll("*"))
            .map(element => ({ element, value:getComputedStyle(element).backgroundImage }))
            .filter(item => item.value && item.value !== "none")
            .slice(0, 50)
            .map(item => {
              const urls = Array.from(item.value.matchAll(/url\(["']?(.*?)["']?\)/g))
                .map(match => match[1]);
              return {
                tag:item.element.tagName,
                id:item.element.id,
                className:String(item.element.className).slice(0, 200),
                backgroundImage:item.value,
                resources:urls.map(url => ({ url, performanceEntries:timingFor(url) }))
              };
            });
          const families = [...new Set([bodyStyle.fontFamily, mainStyle?.fontFamily]
            .filter(Boolean))];
          const fontChecks = families.map(family => ({
            family,
            loaded: document.fonts.check(`16px ${family}`)
          }));
          const media = Array.from(document.querySelectorAll("audio,video")).map(element => ({
            kind: element.tagName.toLowerCase(),
            currentSrc: element.currentSrc || "",
            preload: element.preload,
            paused: element.paused,
            ended: element.ended,
            currentTime: element.currentTime,
            loadedMetadata: element.readyState >= HTMLMediaElement.HAVE_METADATA,
            readyState: element.readyState,
            networkState: element.networkState,
            duration: Number.isFinite(element.duration) ? element.duration : null,
            errorCode: element.error?.code || 0
          }));
          const scrollableContainers = Array.from(document.querySelectorAll("*"))
            .filter(element => {
              const style = getComputedStyle(element);
              return element.scrollHeight > element.clientHeight + 1 &&
                /^(?:auto|scroll|overlay)$/.test(style.overflowY);
            })
            .slice(0, 20)
            .map(element => ({
              tag: element.tagName,
              id: element.id,
              className: String(element.className).slice(0, 200),
              clientHeight: element.clientHeight,
              scrollHeight: element.scrollHeight,
              scrollTop: element.scrollTop,
              overflowY: getComputedStyle(element).overflowY
            }));
          const resources = performance.getEntriesByType("resource");
          let localStoragePersisted = false;
          try {
            localStoragePersisted =
              localStorage.getItem("vdict-smoke-storage") === "offline";
          } catch {}
          const isExternalNetworkResource = ({ name }) => {
            try {
              const url = new URL(name, location.href);
              if (!/^(?:https?|wss?|ftp):$/i.test(url.protocol)) return false;
              const host = url.hostname.toLowerCase();
              const isInternal = url.protocol === "https:" &&
                (host === "reader.vdict.invalid" ||
                 (host.startsWith("d-") && host.endsWith(".vdict.invalid")));
              return !isInternal;
            } catch {
              return false;
            }
          };
          const requestTypes = {};
          for (const resource of resources) {
            const type = String(resource.initiatorType || "other").slice(0, 32);
            requestTypes[type] = (requestTypes[type] || 0) + 1;
          }
          const resourceEntries = resources.slice(0, 200).map(resource => ({
            name:resource.name,
            initiatorType:resource.initiatorType || "",
            duration:resource.duration,
            transferSize:resource.transferSize || 0,
            encodedBodySize:resource.encodedBodySize || 0,
            decodedBodySize:resource.decodedBodySize || 0,
            responseStatus:resource.responseStatus || 0
          }));
          return {
            readyState: document.readyState,
            devicePixelRatio: window.devicePixelRatio,
            htmlClass: document.documentElement.className,
            bodyVisibility: bodyStyle.visibility,
            bodyDisplay: bodyStyle.display,
            bodyOpacity: bodyStyle.opacity,
            htmlPointerEvents: htmlStyle.pointerEvents,
            bodyPointerEvents: bodyStyle.pointerEvents,
            bodyUserSelect: bodyStyle.userSelect,
            hitElement: hitElement ? {
              tag: hitElement.tagName,
              id: hitElement.id,
              className: String(hitElement.className).slice(0, 200)
            } : null,
            overlays,
            bodyColor: bodyStyle.color,
            bodyBackground: bodyStyle.backgroundColor,
            mainVisibility: mainStyle?.visibility || "",
            mainDisplay: mainStyle?.display || "",
            mainOpacity: mainStyle?.opacity || "",
            mainColor: mainStyle?.color || "",
            viewportHeight: innerHeight,
            htmlClientHeight: document.documentElement.clientHeight,
            bodyClientHeight: document.body.clientHeight,
            documentScrollHeight: document.documentElement.scrollHeight,
            bodyScrollHeight: document.body.scrollHeight,
            documentScrollTop: document.documentElement.scrollTop,
            bodyScrollTop: document.body.scrollTop,
            htmlOverflowY: htmlStyle.overflowY,
            bodyOverflowY: bodyStyle.overflowY,
            mainOverflowY: mainStyle?.overflowY || "",
            scrollableContainers,
            mainScrollHeight: mainElement?.scrollHeight || 0,
            mainRect: mainElement?.getBoundingClientRect().toJSON() || null,
            firstChildRect:
              mainElement?.firstElementChild?.getBoundingClientRect().toJSON() || null,
            elementCount: mainElement?.querySelectorAll("*").length || 0,
            scriptCount: document.scripts.length,
            imageCount: images.length,
            decodedImageCount: images.filter(image => image.complete && image.naturalWidth > 0).length,
            requestCount: resources.length,
            remoteRequestCount: resources.filter(isExternalNetworkResource).length,
            requestTypes,
            fontStatus: document.fonts.status,
            fontChecks,
            media,
            localScripts: {
              classicLoaded: window.vdictFixture?.classicLoaded === true,
              moduleResult: window.vdictModuleResult ?? null,
              fetchResult: window.vdictFetchResult ?? null,
              localStoragePersisted,
              legacyEvalResult: window.vdictLegacyEval ?? null
            },
            images,
            backgroundResources,
            resourceEntries:resourceEntries.slice(0, 50)
          };
        })())
        """;
}
