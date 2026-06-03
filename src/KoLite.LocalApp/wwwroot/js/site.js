(function () {
  var sliceTooltip;
  var successChartPalette = [
    "#0969da",
    "#1a7f37",
    "#8250df",
    "#bf3989",
    "#9a6700",
    "#cf222e",
    "#1b7c83",
    "#6f42c1",
    "#d4a72c",
    "#57606a"
  ];

  function tooltipRow(label, value) {
    var row = document.createElement("div");
    row.className = "tooltip-row";

    var labelNode = document.createElement("span");
    labelNode.className = "tooltip-label";
    labelNode.textContent = label;
    row.appendChild(labelNode);

    var valueNode = document.createElement("span");
    valueNode.className = "tooltip-value";
    valueNode.textContent = value || "-";
    row.appendChild(valueNode);

    return row;
  }

  function ensureSliceTooltip() {
    if (sliceTooltip) return sliceTooltip;

    sliceTooltip = document.createElement("div");
    sliceTooltip.className = "slice-history-tooltip";
    sliceTooltip.setAttribute("role", "tooltip");
    document.body.appendChild(sliceTooltip);
    return sliceTooltip;
  }

  function positionTooltip(target, tooltip) {
    var rect = target.getBoundingClientRect();
    var margin = 8;
    var left = Math.max(margin, Math.min(rect.left, window.innerWidth - tooltip.offsetWidth - margin));
    var top = rect.top - tooltip.offsetHeight - margin;
    if (top < margin) top = rect.bottom + margin;
    tooltip.style.left = left + "px";
    tooltip.style.top = top + "px";
  }

  function sliceTooltipRows(target) {
    var lineNodes = target.querySelectorAll("[data-slice-tooltip-line='true']");
    if (lineNodes.length) {
      return Array.prototype.map.call(lineNodes, function (lineNode) {
        return {
          label: lineNode.getAttribute("data-tooltip-label"),
          value: lineNode.getAttribute("data-tooltip-value")
        };
      });
    }

    return [
      { label: "Start", value: target.getAttribute("data-slice-start") },
      { label: "End", value: target.getAttribute("data-slice-end") },
      { label: "Status", value: target.getAttribute("data-slice-status") },
      { label: "Attempt", value: target.getAttribute("data-slice-attempt") }
    ];
  }

  function showSliceTooltip(target) {
    var tooltip = ensureSliceTooltip();
    tooltip.classList.remove("visible");
    tooltip.replaceChildren();

    var title = document.createElement("div");
    title.className = "tooltip-title";
    title.textContent = target.getAttribute("data-slice-tooltip-title") || "Slice";
    tooltip.appendChild(title);

    sliceTooltipRows(target).forEach(function (row) {
      tooltip.appendChild(tooltipRow(row.label, row.value));
    });

    tooltip.style.left = "0";
    tooltip.style.top = "0";
    positionTooltip(target, tooltip);
    tooltip.classList.add("visible");
  }

  function hideSliceTooltip() {
    if (sliceTooltip) sliceTooltip.classList.remove("visible");
  }

  function formatUtcTick(value, rangeMs) {
    var date = new Date(Number(value));
    if (Number.isNaN(date.getTime())) return "";

    var month = String(date.getUTCMonth() + 1).padStart(2, "0");
    var day = String(date.getUTCDate()).padStart(2, "0");
    var hour = String(date.getUTCHours()).padStart(2, "0");
    var minute = String(date.getUTCMinutes()).padStart(2, "0");
    if (rangeMs <= 60 * 60 * 1000) return hour + ":" + minute;
    if (rangeMs <= 24 * 60 * 60 * 1000) return month + "/" + day + " " + hour + ":" + minute;
    return month + "/" + day;
  }

  function toggleSuccessRateSeries(event, legendItem, legend) {
    var chart = legend.chart;
    var datasetIndex = legendItem.datasetIndex;
    var allVisible = chart.data.datasets.every(function (_, index) {
      return chart.isDatasetVisible(index);
    });

    if (allVisible) {
      chart.data.datasets.forEach(function (_, index) {
        chart.setDatasetVisibility(index, index === datasetIndex);
      });
      chart.update();
      return;
    }

    chart.setDatasetVisibility(datasetIndex, !chart.isDatasetVisible(datasetIndex));
    var anyVisible = chart.data.datasets.some(function (_, index) {
      return chart.isDatasetVisible(index);
    });
    if (!anyVisible) {
      chart.data.datasets.forEach(function (_, index) {
        chart.setDatasetVisibility(index, true);
      });
    }

    chart.update();
  }

  function buildSuccessRateChart(canvas, payload) {
    if (!window.Chart || !canvas || !payload || !payload.series || payload.series.length === 0) return null;

    var rangeStart = Date.parse(payload.rangeStartUtc);
    var rangeEnd = Date.parse(payload.rangeEndUtc);
    var rangeMs = Math.max(0, rangeEnd - rangeStart);
    var datasets = payload.series.map(function (series, index) {
      var color = successChartPalette[index % successChartPalette.length];
      return {
        label: series.name,
        data: series.points.map(function (point) {
          return {
            x: point.x,
            y: point.y,
            numerator: point.numerator,
            denominator: point.denominator,
            bucket: point.bucket,
            label: point.label,
            percentText: point.percentText
          };
        }),
        borderColor: color,
        backgroundColor: color,
        borderWidth: 2.25,
        pointBorderColor: "#fff",
        pointBorderWidth: 1.25,
        pointHitRadius: 10,
        pointHoverRadius: 5,
        pointRadius: function (context) {
          return context.raw && context.raw.y !== null ? 2.75 : 0;
        },
        tension: 0.22,
        spanGaps: false
      };
    });

    return new Chart(canvas, {
      type: "line",
      data: { datasets: datasets },
      options: {
        animation: false,
        maintainAspectRatio: false,
        normalized: true,
        parsing: false,
        interaction: {
          intersect: false,
          mode: "nearest"
        },
        plugins: {
          legend: {
            display: true,
            position: "bottom",
            labels: {
              boxWidth: 28,
              color: "#24292f",
              font: { size: 12 },
              usePointStyle: true
            },
            onClick: toggleSuccessRateSeries
          },
          tooltip: {
            callbacks: {
              title: function (items) {
                var raw = items.length ? items[0].raw : null;
                return raw ? raw.bucket : "";
              },
              label: function (context) {
                var raw = context.raw || {};
                var value = raw.y === null || typeof raw.y === "undefined" ? "n/a" : raw.percentText;
                var count = typeof raw.numerator === "number" && typeof raw.denominator === "number"
                  ? " (" + raw.numerator + "/" + raw.denominator + ")"
                  : "";
                return context.dataset.label + ": " + value + count;
              }
            }
          }
        },
        scales: {
          x: {
            type: "linear",
            min: rangeStart,
            max: rangeEnd,
            grid: { color: "rgba(208, 215, 222, 0.55)" },
            ticks: {
              color: "#57606a",
              maxRotation: 0,
              callback: function (value) { return formatUtcTick(value, rangeMs); }
            }
          },
          y: {
            min: 0,
            max: 100,
            grid: { color: "rgba(208, 215, 222, 0.75)" },
            ticks: {
              color: "#57606a",
              stepSize: 25,
              callback: function (value) { return value + "%"; }
            },
            title: {
              display: true,
              text: "Success",
              color: "#57606a"
            }
          }
        }
      }
    });
  }

  function initSuccessRateCharts() {
    document.querySelectorAll("[data-chartjs-success]").forEach(function (container) {
      var chartId = container.getAttribute("data-chartjs-success");
      var canvas = document.getElementById(chartId);
      var payloadNode = document.getElementById(chartId + "-data");
      if (!canvas || !payloadNode) return;

      try {
        var payload = JSON.parse(payloadNode.textContent || "{}");
        buildSuccessRateChart(canvas, payload);
      } catch (error) {
        container.classList.add("chart-error");
        var message = document.createElement("p");
        message.className = "empty";
        message.textContent = "Chart data could not be rendered.";
        container.prepend(message);
        throw error;
      }
    });
  }

  document.addEventListener("mouseover", function (event) {
    var target = event.target;
    if (!(target instanceof HTMLElement)) return;
    var cell = target.closest("[data-slice-tooltip='true']");
    if (cell) showSliceTooltip(cell);
  });

  document.addEventListener("mouseout", function (event) {
    var target = event.target;
    if (!(target instanceof HTMLElement)) return;
    var cell = target.closest("[data-slice-tooltip='true']");
    if (cell && (!(event.relatedTarget instanceof Node) || !cell.contains(event.relatedTarget))) {
      hideSliceTooltip();
    }
  });

  document.addEventListener("focusin", function (event) {
    var target = event.target;
    if (target instanceof HTMLElement && target.matches("[data-slice-tooltip='true']")) {
      showSliceTooltip(target);
    }
  });

  document.addEventListener("focusout", function (event) {
    var target = event.target;
    if (target instanceof HTMLElement && target.matches("[data-slice-tooltip='true']")) {
      hideSliceTooltip();
    }
  });

  document.addEventListener("click", function (event) {
    var target = event.target;
    if (!(target instanceof HTMLElement)) return;

    var copyTarget = target.getAttribute("data-copy-target");
    if (copyTarget) {
      var input = document.querySelector("[name='" + copyTarget + "']");
      if (input && "value" in input && navigator.clipboard) {
        navigator.clipboard.writeText(input.value);
        target.textContent = "Copied";
        setTimeout(function () { target.textContent = "Copy JSON"; }, 1200);
      }
    }

    var confirmText = target.getAttribute("data-confirm-text");
    if (confirmText) {
      var form = target.closest("form");
      var confirmation = form ? form.querySelector("[name='confirmation']") : null;
      if (!confirmation || confirmation.value !== confirmText) {
        event.preventDefault();
        alert("Type exactly: " + confirmText);
      }
    }
  });

  window.BuildSuccessRateChart = buildSuccessRateChart;
  window.toggleSuccessRateSeries = toggleSuccessRateSeries;
  initSuccessRateCharts();
})();
