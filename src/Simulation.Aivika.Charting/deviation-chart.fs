
// Aivika for .NET
// Copyright (c) 2009-2015  David Sorokin. All rights reserved.
//
// This file is a part of Aivika for .NET
//
// Commercial License Usage
// Licensees holding valid commercial Aivika licenses may use this file in
// accordance with the commercial license agreement provided with the
// Software or, alternatively, in accordance with the terms contained in
// a written agreement between you and David Sorokin, Yoshkar-Ola, Russia. 
// For the further information contact <mailto:david.sorokin@gmail.com>.
//
// GNU General Public License Usage
// Alternatively, this file may be used under the terms of the GNU
// General Public License version 3 or later as published by the Free
// Software Foundation and appearing in the file LICENSE.GPLv3 included in
// the packaging of this file.  Please review the following information to
// ensure the GNU General Public License version 3 requirements will be
// met: http://www.gnu.org/licenses/gpl-3.0.html.

namespace Simulation.Aivika.Charting.Web

open System
open System.IO
open System.Web.UI
open System.Globalization
open System.Drawing
open System.Windows.Forms.DataVisualization.Charting

open Simulation.Aivika
open Simulation.Aivika.Results
open Simulation.Aivika.Experiments
open Simulation.Aivika.Charting

type DeviationChartProvider () as provider =

    let mutable title = "Deviation Chart"
    let mutable description = "It shows the Deviation chart by rule 3-sigma."
    let mutable width = 640
    let mutable height = 480
    let mutable file = UniqueFilePath "DeviationChart"
    let mutable transform: ResultTransform = id
    let mutable series: ResultTransform = id 
    let mutable seriesColors = Array.copy Color.predefined

    member x.Title with get () = title and set v = title <- v
    member x.Description with get () = description and set v = description <- v
    member x.Width with get () = width and set v = width <- v
    member x.Height with get () = height and set v = height <- v
    member x.File with get () = file and set v = file <- v
    member x.Transform with get () = transform and set v = transform <- v
    member x.Series with get () = series and set v = series <- v
    member x.SeriesColors with get () = seriesColors and set v = seriesColors <- v 

    interface IExperimentProvider<HtmlTextWriter> with
        member x.CreateRenderer (ctx) =

            let exp = ctx.Experiment 
            let writer = ctx.Writer
            let formatInfo = exp.FormatInfo
            let runCount = exp.RunCount
            let specs = exp.Specs

            let filename = 
                provider.File 
                    |> ExperimentFilePath.changeExtension "png"
            let filename =
                ctx.Resolve (ctx.Directory, filename)

            let n = specs.IterationCount
            let times = specs.IntegTimes |> Seq.toArray

            assert (times.Length = n)

            let names = ref [| |]
            let stats = ref [| |]

            let lockobj = obj ()

            { new IExperimentRenderer with
                member x.BeginRendering () = ()
                member x.Simulate (signals, results) = 
                    eventive {

                        let results = results |> provider.Transform 
                        let results = results |> provider.Series

                        let values = results |> ResultSet.floatStatsChoiceValues |> Seq.toArray

                        let names' = values |> Array.map (fun v -> v.Name)
                        let data'  = values |> Array.map (fun v -> v.Data)

                        lock lockobj (fun () ->
                            if (!names).Length = 0 then
                                names := names'
                                stats := names' |> Array.map (fun v -> Array.create n SamplingStats.emptyFloats)
                            elif (!names).Length <> names'.Length then
                                failwithf "Series of different lengths are returned for different runs when plotting the deviation chart.")

                        let handle a =
                            eventive {
                                let! iteration = Dynamics.integIteration |> Dynamics.lift
                                for i = 0 to data'.Length - 1 do
                                    let! st = data'.[i]
                                    lock lockobj (fun () ->
                                        (!stats).[i].[iteration] <-
                                            (!stats).[i].[iteration]
                                                |> SamplingStats.appendChoice st)
                            }

                        return! 
                            signals.SignalInIntegTimes
                                |> Signal.subscribe handle 
                    }
                member x.EndRendering () =

                    let finite x = not (Double.IsNaN (x) || Double.IsInfinity (x))

                    let lineColor i =
                        let cs = provider.SeriesColors
                        cs.[i % cs.Length]

                    let barColor i =
                        let c = lineColor i
                        Color.FromArgb (100, int c.R, int c.G, int c.B)

                    let line i =

                        let name = (!names).[i]

                        let ys = (!stats).[i] |> Array.map (fun st -> st.Mean) 

                        let (times, ys) = 
                            Array.zip times ys
                                |> Array.filter (fun (t, y) -> finite t && finite y)
                                |> Array.unzip
                        
                        let series = new Series ()
                                        
                        series.BorderColor <- lineColor i
                        series.Color <- lineColor i 
                        series.LegendText <- name
                        series.ChartType <- SeriesChartType.Line
                        series.Points.DataBindXY(times, ys)
                        series

                    let bar i =

                        let name = (!names).[i]

                        let ys1 = (!stats).[i] |> Array.map (fun st -> st.Mean - 3.0 * st.Deviation) 
                        let ys2 = (!stats).[i] |> Array.map (fun st -> st.Mean + 3.0 * st.Deviation)

                        let (times, ys1, ys2) = 
                            Array.zip3 times ys1 ys2
                                |> Array.filter (fun (t, y1, y2) -> finite t && finite y1 && finite y2)
                                |> Array.unzip3

                        let series = new Series ()
                                        
                        series.BorderColor <- barColor i
                        series.Color <- barColor i 
                        series.IsVisibleInLegend <- false
                        series.ChartType <- SeriesChartType.Range
                        series.YValuesPerPoint <- 2
                        series.Points.DataBindXY(times, ys1, ys2)
                        series

                    let chart = new Chart ()
                    chart.Width <- provider.Width
                    chart.Height <- provider.Height

                    let area = new ChartArea ()
                    area.AxisX.Title <- "t"

                    chart.ChartAreas.Add (area)

                    let legend = new Legend ()
                    legend.Docking <- Docking.Bottom

                    chart.Legends.Add(legend)

                    let bars = 
                        [| for i = 0 to (!names).Length - 1 do
                            yield bar i |]

                    let lines =
                        [| for i = 0 to (!names).Length - 1 do
                            yield line i |]

                    for i = 0 to (!names).Length - 1 do

                        let s1 = bars.[i]
                        let s2 = lines.[i]
                        
                        chart.Series.Add (s1)
                        chart.Series.Add (s2)

                    chart.SaveImage (filename, ChartImageFormat.Png)
                    chart.Dispose ()

                    if exp.Verbose then
                        printfn "Generated file %s" filename

                    let getLink filename =
                        Uri.EscapeUriString (Path.GetFileName (filename))

                    writer.WriteFullBeginTag ("h3")
                    writer.WriteEncodedText (provider.Title)
                    writer.WriteEndTag ("h3")

                    if provider.Description <> "" then

                        writer.WriteFullBeginTag ("p")
                        writer.WriteEncodedText (provider.Description)
                        writer.WriteEndTag ("p")

                    writer.WriteFullBeginTag ("p")
                    writer.WriteBeginTag ("image")
                    writer.WriteAttribute ("src", getLink filename)
                    writer.Write (">")
                    writer.WriteEndTag ("image")
                    writer.WriteEndTag ("p")
            }
