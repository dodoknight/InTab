﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Emgu.CV;
using Emgu.CV.Structure;
using System.Threading.Tasks;
using InteractiveTable.Core.Data.Capture;
using System.Drawing;

namespace InteractiveTable.Core.Graphics {

    /// <summary>
    /// Class that process a captured frame, finds all contours and saves them into a collection
    /// </summary>
    public class ContourFilter {
        public bool noiseFilter = false; // noise filter
        public int cannyThreshold = 50; // max size of contours
        public bool blur = true; // blurring
        public int adaptiveThresholdBlockSize = 4; // treshold of areas of checked blocks
        public double adaptiveThresholdParameter = 1.2d; // parameter of the treshold
        public bool filterContoursBySize = true; // filter contours by size
        public bool onlyFindContours = false; // find contours without applying additional effects
        public int minContourLength = 15; // min length of contours to detect
        public int minContourArea = 10; // min area of contours to detect
        public double minFormFactor = 0.5; // min factor for contour filtering

        public List<Contour<Point>> contours; // contours
        public Templates templates = new Templates(); // templates
        public Templates samples = new Templates(); // samples
        public List<FoundTemplateDesc> foundTemplates = new List<FoundTemplateDesc>(); // detected templates
        public TemplateFinder finder = new TemplateFinder(); // template finder
        public Image<Gray, byte> binarizedFrame; // binarized captured image


        /// <summary>
        /// Processes image in RGB colors
        /// </summary>
        public void ProcessImage(Image<Bgr, byte> frame) {
            ProcessImage(frame.Convert<Gray, Byte>());
        }

        /// <summary>
        /// Processes image in gray colors
        /// </summary>
        public void ProcessImage(Image<Gray, byte> grayFrame) {
            // smoothing -> downscaling and upscaling via binary interpolation
            Image<Gray, byte> smoothedGrayFrame = grayFrame.PyrDown();
            smoothedGrayFrame = smoothedGrayFrame.PyrUp();

            Image<Gray, byte> cannyFrame = null;

            if (noiseFilter) cannyFrame = smoothedGrayFrame.Canny(new Gray(cannyThreshold), new Gray(cannyThreshold));
            if (blur) grayFrame = smoothedGrayFrame;

            // transform into binary image (2 values)
            CvInvoke.cvAdaptiveThreshold(grayFrame, grayFrame, 255, Emgu.CV.CvEnum.ADAPTIVE_THRESHOLD_TYPE.CV_ADAPTIVE_THRESH_MEAN_C, Emgu.CV.CvEnum.THRESH.CV_THRESH_BINARY, adaptiveThresholdBlockSize + adaptiveThresholdBlockSize % 2 + 1, adaptiveThresholdParameter);

            // invert
            grayFrame._Not();

            // logic OR will smooth additional noise
            if (cannyFrame != null) grayFrame._Or(cannyFrame);

            this.binarizedFrame = grayFrame;

            if (cannyFrame != null) cannyFrame = cannyFrame.Dilate(3);

            // find all contours
            var sourceContours = grayFrame.FindContours(Emgu.CV.CvEnum.CHAIN_APPROX_METHOD.CV_CHAIN_APPROX_NONE, Emgu.CV.CvEnum.RETR_TYPE.CV_RETR_LIST);
            // filter all contours    
            contours = FilterContours(sourceContours, cannyFrame, grayFrame.Width, grayFrame.Height);

            lock (foundTemplates)
                foundTemplates.Clear();
            samples.Clear();

            // process all contours in parallel
            lock (templates)
                Parallel.ForEach<Contour<Point>>(contours, (contour) => {
                    // create a template sample from each contour and try to find its relevant template
                    var arr = contour.ToArray();
                    Template sample = new Template(arr, contour.Area, samples.templateSize);
                    lock (samples)
                        samples.Add(sample);

                    if (!onlyFindContours) {
                        // find a template according to the sample
                        FoundTemplateDesc desc = finder.FindTemplate(templates, sample);

                        if (desc != null)
                            lock (foundTemplates)
                                foundTemplates.Add(desc);
                    }
                }
                );
            FilterByIntersection(ref foundTemplates);
        }

        /// <summary>
        /// Filters found contours according to their size, filter etc..
        /// </summary>
        /// <returns></returns>
        private List<Contour<Point>> FilterContours(Contour<Point> contours, Image<Gray, byte> cannyFrame, int frameWidth, int frameHeight) {
            int maxArea = frameWidth * frameHeight / 5;
            var c = contours;
            List<Contour<Point>> result = new List<Contour<Point>>();
            while (c != null) {
                if (filterContoursBySize)
                    if (c.Total < minContourLength ||
                        c.Area < minContourArea || c.Area > maxArea ||
                        c.Area / c.Total <= minFormFactor)
                        goto next;

                if (noiseFilter) {
                    Point p1 = c[0];
                    Point p2 = c[(c.Total / 2) % c.Total];
                    if (cannyFrame[p1].Intensity <= double.Epsilon && cannyFrame[p2].Intensity <= double.Epsilon)
                        goto next;
                }
                result.Add(c);

                next:
                c = c.HNext;
            }

            return result;
        }

        /// <summary>
        /// Filters found contours by intersection
        /// </summary>
        private static void FilterByIntersection(ref List<FoundTemplateDesc> templates) {
            // order the templates by the area they are located in
            templates.Sort(new Comparison<FoundTemplateDesc>((t1, t2) => -t1.sample.contour.SourceBoundingRectArea().CompareTo(t2.sample.contour.SourceBoundingRectArea())));
            
            // delete templates inside other templates
            HashSet<int> toDel = new HashSet<int>();
            for (int i = 0; i < templates.Count; i++) {
                if (toDel.Contains(i))
                    continue;
                Rectangle bigRect = templates[i].sample.contour.SourceBoundingRect;
                int bigArea = templates[i].sample.contour.SourceBoundingRectArea();
                bigRect.Inflate(4, 4);
                for (int j = i + 1; j < templates.Count; j++) {
                    if (bigRect.Contains(templates[j].sample.contour.SourceBoundingRect)) {
                        double a = templates[j].sample.contour.SourceBoundingRectArea();
                        if (a / bigArea > 0.9d) {

                            if (templates[i].rate > templates[j].rate)
                                toDel.Add(j);
                            else
                                toDel.Add(i);
                        } else//smaze sablonu
                            toDel.Add(j);
                    }
                }
            }
            List<FoundTemplateDesc> newTemplates = new List<FoundTemplateDesc>();
            for (int i = 0; i < templates.Count; i++)
                if (!toDel.Contains(i))
                    newTemplates.Add(templates[i]);
            templates = newTemplates;
        }
    }
}
